namespace AiAssistant.Api.Configuration;

public class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string Host { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "qwen3:8b";
    public string EmbedModel { get; set; } = "qwen3-embedding:8b";
    public double TimeoutMinutes { get; set; } = 10;

    // Ollama defaults qwen3:8b to a 40K context window; its KV cache alone
    // costs several GB of RAM at that size. 8192 is enough for a RAG prompt
    // (question + retrieved chunks) without paying for context nothing uses.
    public int ChatContextTokens { get; set; } = 8192;
}
