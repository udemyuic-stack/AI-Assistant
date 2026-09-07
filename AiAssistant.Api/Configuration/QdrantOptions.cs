namespace AiAssistant.Api.Configuration;

public class QdrantOptions
{
    public const string SectionName = "Qdrant";

    public string Host { get; set; } = "localhost";
    public int GrpcPort { get; set; } = 6334;
    public string Collection { get; set; } = "documents";

    // Must match the embedding model's output dimension. qwen3-embedding:8b
    // emits 4096; nomic-embed-text emits 768. A mismatch is rejected by
    // Qdrant at upsert time, not at startup.
    public ulong VectorSize { get; set; } = 4096;
}
