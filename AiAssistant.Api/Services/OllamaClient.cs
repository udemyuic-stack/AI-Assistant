using System.Runtime.CompilerServices;
using System.Text.Json;
using AiAssistant.Api.Configuration;
using Microsoft.Extensions.Options;
using Refit;

namespace AiAssistant.Api.Services;

// Chat and embeddings, with model names applied and transport failures turned
// into one exception type. No inference runs here - Ollama hosts the model and
// this process only exchanges JSON with it.
//
// Generation goes through /api/generate (a flat prompt) rather than /api/chat
// (role-structured messages), even though the external shape of
// StreamChatAsync still takes (role, content) pairs. Measured on a similar
// system: an 8B model handles a flat prompt more reliably than a chat
// template, which depends on how the specific fine-tune was trained to parse
// role markers. The public method keeps its shape so callers don't need to
// know which endpoint answers it - that decision belongs here, and may
// reverse itself on a larger model with a properly-tuned chat template.
public class OllamaClient(IOllamaApi api, IOptions<OllamaOptions> options)
{
    private readonly OllamaOptions _options = options.Value;

    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<(string Role, string Content)> messages,
        double temperature,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (system, prompt) = FlattenToPrompt(messages);

        var request = new OllamaGenerateRequest(
            _options.ChatModel,
            prompt,
            system,
            new OllamaChatOptions(temperature, _options.ChatContextTokens),
            _options.ChatKeepAlive);

        // Opened outside the iterator loop: a try/catch cannot wrap a yield.
        var stream = await OpenGenerateStreamAsync(request, ct);

        await foreach (var token in ReadGenerateTokensAsync(stream, ct))
            yield return token;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var vectors = await EmbedBatchAsync([text], ct);
        return vectors[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        OllamaEmbedResponse response;
        try
        {
            response = await api.EmbedAsync(
                new OllamaEmbedRequest(_options.EmbedModel, texts, _options.EmbedKeepAlive), ct);
        }
        catch (ApiException ex)
        {
            throw Unavailable($"'{_options.EmbedModel}' modeli javob bermadi", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable("ulanib bo'lmadi", ex);
        }

        // A short batch would silently misalign chunks and vectors downstream,
        // so it fails here rather than corrupting the index.
        var vectors = response.Embeddings;
        if (vectors is null || vectors.Length != texts.Count)
            throw new OllamaUnavailableException(
                $"Ollama '{_options.EmbedModel}' modelidan {vectors?.Length ?? 0} ta vektor qaytardi, " +
                $"{texts.Count} ta kutilgan edi.");

        return vectors;
    }

    // Ollama's own "system" field is a distinct parameter from the prompt, so
    // only the non-system messages fold into one block. Current callers pass
    // exactly one of each, but this holds for any number of user/assistant
    // turns a future history feature might add.
    private static (string? System, string Prompt) FlattenToPrompt(
        IEnumerable<(string Role, string Content)> messages)
    {
        string? system = null;
        var rest = new List<string>();

        foreach (var (role, content) in messages)
        {
            if (role == "system") system = content;
            else rest.Add(content);
        }

        return (system, string.Join("\n\n", rest));
    }

    private async Task<Stream> OpenGenerateStreamAsync(OllamaGenerateRequest request, CancellationToken ct)
    {
        try
        {
            return await api.StreamGenerateAsync(request, ct);
        }
        catch (ApiException ex)
        {
            throw Unavailable($"'{_options.ChatModel}' modeli javob bermadi", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable("ulanib bo'lmadi", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Unavailable($"{_options.TimeoutMinutes} daqiqada javob bermadi", ex);
        }
    }

    private static async IAsyncEnumerable<string> ReadGenerateTokensAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;

                var token = ParseGenerateToken(line);
                if (!string.IsNullOrEmpty(token))
                    yield return token;
            }
        }
    }

    private static string? ParseGenerateToken(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("response", out var response)
                ? response.GetString()
                : null;
        }
        catch (JsonException)
        {
            // One malformed line must not discard an answer already in flight.
            return null;
        }
    }

    private OllamaUnavailableException Unavailable(string reason, Exception inner) =>
        new($"Ollama ({_options.Host}) {reason}.", inner);
}
