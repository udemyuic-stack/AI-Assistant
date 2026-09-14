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

    // Fixes a named limitation from the book: a query in one script/language
    // ("железка") can't find a document indexed under a different one
    // ("temir yo'l") - stem matching only bridges inflections of the same
    // word, not synonyms across languages. Costs one extra Ollama call per
    // question, so it defaults off until the CPU-only host isn't the bottleneck.
    public bool QueryExpansionEnabled { get; set; } = false;

    // HyDE: embed a hypothetical answer instead of the question. A question
    // and the passage answering it are worded differently ("Litsenziya necha
    // kunda beriladi?" vs "Litsenziya 30 kun ichida beriladi"), so a made-up
    // answer often sits closer to the real passage than the question does.
    // Costs one more model call on top of expansion - off until the host has
    // a GPU. The guide marks it optional for the same reason.
    public bool HydeEnabled { get; set; } = false;

    // Sentence-window retrieval: after ranking, pull in this many chunks on
    // each side of every kept chunk from the same source document. A fact
    // split across a chunk boundary - a figure in one sentence, what it
    // counts introduced in the next - can rank low on either half alone even
    // though the whole passage clearly answers the question; the neighbour
    // is fetched by an exact (objectKey, chunkIndex) match, not a second
    // vector search, so it costs a Qdrant scroll, not another embedding.
    // 0 disables it and reproduces the exact old behaviour.
    public int RetrievalWindowSize { get; set; } = 1;
}
