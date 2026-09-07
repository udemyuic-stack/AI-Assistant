using System.Runtime.CompilerServices;
using System.Text.Json;
using AiAssistant.Api.Configuration;
using Microsoft.Extensions.Options;
using Refit;

namespace AiAssistant.Api.Services;

// Chat and embeddings, with model names applied and transport failures turned
// into one exception type. No inference runs here - Ollama hosts the model and
// this process only exchanges JSON with it.
public class OllamaClient(IOllamaApi api, IOptions<OllamaOptions> options)
{
    private readonly OllamaOptions _options = options.Value;

    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<(string Role, string Content)> messages,
        double temperature,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new OllamaChatRequest(
            _options.ChatModel,
            messages.Select(m => new OllamaChatMessage(m.Role, m.Content)).ToList(),
            new OllamaChatOptions(temperature, _options.ChatContextTokens));

        // Opened outside the iterator loop: a try/catch cannot wrap a yield.
        var stream = await OpenChatStreamAsync(request, ct);

        await foreach (var token in ReadTokensAsync(stream, ct))
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
            response = await api.EmbedAsync(new OllamaEmbedRequest(_options.EmbedModel, texts), ct);
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

    private async Task<Stream> OpenChatStreamAsync(OllamaChatRequest request, CancellationToken ct)
    {
        try
        {
            return await api.StreamChatAsync(request, ct);
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

    private static async IAsyncEnumerable<string> ReadTokensAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using (stream)
        {
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;

                var token = ParseToken(line);
                if (!string.IsNullOrEmpty(token))
                    yield return token;
            }
        }
    }

    private static string? ParseToken(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("message", out var message) &&
                   message.TryGetProperty("content", out var content)
                ? content.GetString()
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
