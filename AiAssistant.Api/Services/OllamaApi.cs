using System.Text.Json.Serialization;
using Refit;

namespace AiAssistant.Api.Services;

// Ollama's HTTP surface, declared rather than hand-built. Refit owns
// serialisation, the base address, and the request pipeline, so the client
// below is left with the parts that actually carry logic.
public interface IOllamaApi
{
    // Returns the raw stream: Ollama answers with newline-delimited JSON, one
    // object per token, and Refit reads a Stream return type without buffering.
    [Post("/api/chat")]
    Task<Stream> StreamChatAsync([Body] OllamaChatRequest request, CancellationToken ct);

    // A raw prompt instead of a role-structured message list. An 8B model's
    // chat template can be applied inconsistently by whatever fine-tune
    // produced it; a flat prompt sidesteps that entirely. Kept alongside
    // StreamChatAsync rather than replacing it, since a larger model on
    // better hardware may prefer the structured form again.
    [Post("/api/generate")]
    Task<Stream> StreamGenerateAsync([Body] OllamaGenerateRequest request, CancellationToken ct);

    [Post("/api/embed")]
    Task<OllamaEmbedResponse> EmbedAsync([Body] OllamaEmbedRequest request, CancellationToken ct);
}

public record OllamaChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage> Messages,
    [property: JsonPropertyName("options")] OllamaChatOptions Options)
{
    [JsonPropertyName("stream")]
    public bool Stream => true;
}

public record OllamaChatOptions(
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("num_ctx")] int ContextTokens);

public record OllamaGenerateRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("system")] string? System,
    [property: JsonPropertyName("options")] OllamaChatOptions Options)
{
    [JsonPropertyName("stream")]
    public bool Stream => true;
}

public record OllamaEmbedRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

public record OllamaEmbedResponse(
    [property: JsonPropertyName("embeddings")] float[][]? Embeddings);
