using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiAssistant.Eval;

// The two calls this harness makes against the running API.
//
// Raw HttpClient rather than Refit, against the house convention, for one
// reason: the value Refit buys is a mockable interface, and the thing this
// tool exists to exercise is the real service. There is nothing here to mock,
// and two endpoints is not enough boilerplate to be worth a dependency.
public class AssistantProbe(HttpClient http, string adminKey)
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    // Retrieval only: one embedding call, no generation. ~0.3s against ~60s,
    // which is what makes iterating on chunking and thresholds practical.
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string question, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/evaluation/retrieval")
        {
            Content = JsonContent(new { question })
        };
        request.Headers.Add("X-Admin-Key", adminKey);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<RetrievalProbeResponse>(Json, ct);
        return payload?.Chunks ?? [];
    }

    // Full path, including the model. The SSE stream is read to completion so
    // a partial answer never counts as a pass.
    public async Task<string> AnswerAsync(string question, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = JsonContent(new { message = question, userId = "eval-harness" })
        };

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var answer = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            using var document = JsonDocument.Parse(line[6..]);
            var root = document.RootElement;

            if (root.TryGetProperty("token", out var token))
                answer.Append(token.GetString());

            // An error event ends the answer; returning what arrived before it
            // would silently grade a truncated response.
            if (root.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
        }

        return answer.ToString().Trim();
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");
}

public record RetrievalProbeResponse(IReadOnlyList<RetrievedChunk> Chunks);
public record RetrievedChunk(int Rank, float Score, string Source, string Scope, string Preview);
