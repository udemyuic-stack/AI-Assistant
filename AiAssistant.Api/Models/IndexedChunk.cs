namespace AiAssistant.Api.Models;

// One chunk as it landed in the vector store. QdrantPointId is what a
// ContentChunk audit row needs to point back at its vector.
public record IndexedChunk(ulong QdrantPointId, string Text);
