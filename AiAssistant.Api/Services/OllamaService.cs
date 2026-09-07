using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiAssistant.Api.Services;

// Talks to Ollama over plain HTTP. No PyTorch/ONNX in this process — Ollama
// hosts the model, we only send/receive JSON. Language of the caller is
// irrelevant to inference speed or quality; only the model matters.
public class OllamaService(IHttpClientFactory factory, IConfiguration config)
{
    private readonly string _chatModel = config["Ollama:ChatModel"] ?? "qwen3:latest";
    private readonly string _embedModel = config["Ollama:EmbedModel"] ?? "qwen3-embedding:8b";

    public async IAsyncEnumerable<string> ChatStreamAsync(
        IEnumerable<(string Role, string Content)> messages,
        double temperature = 0,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var http = factory.CreateClient("ollama");

        var body = JsonSerializer.Serialize(new
        {
            model = _chatModel,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            stream = true,
            options = new { temperature }
        });

        HttpResponseMessage? response = null;
        string? errorMsg = null;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            errorMsg = $"[Xato: {ex.Message}. Ollama ishlab turganini va model yuklanganini tekshiring.]";
        }

        if (errorMsg is not null) { yield return errorMsg; yield break; }

        await using var stream = await response!.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;

            string? content = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var c))
                    content = c.GetString();
            }
            catch { continue; }

            if (!string.IsNullOrEmpty(content))
                yield return content;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results.FirstOrDefault() ?? [];
    }

    public async Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var http = factory.CreateClient("ollama");

        try
        {
            var response = await http.PostAsJsonAsync("/api/embed", new
            {
                model = _embedModel,
                input = texts
            }, ct);

            if (!response.IsSuccessStatusCode) return [];

            var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct);
            return result?.Embeddings?.Select(e => e).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private record EmbedResponse([property: JsonPropertyName("embeddings")] float[][]? Embeddings);
}
