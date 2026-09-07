namespace AiAssistant.Api.Configuration;

public class RagOptions
{
    public const string SectionName = "Rag";

    // Candidates pulled from the vector store before trimming.
    public int TopK { get; set; } = 15;

    // How many of those candidates reach the prompt.
    public int ContextChunks { get; set; } = 5;

    public float SimilarityThreshold { get; set; } = 0.4f;

    // 0 = deterministic. Anything higher lets the model paraphrase the
    // retrieved context, which is exactly what we do not want here.
    public double Temperature { get; set; } = 0.0;

    // Chunking is measured in characters, not tokens: an exact token count
    // needs the model's tokenizer loaded per chunk, and the estimate only has
    // to be conservative, not precise.
    public int ChunkSize { get; set; } = 1000;
    public int ChunkOverlap { get; set; } = 200;

    public bool RerankEnabled { get; set; } = false;
}
