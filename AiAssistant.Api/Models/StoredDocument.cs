namespace AiAssistant.Api.Models;

// A document that reached object storage. ObjectKey is what survives: the
// vectors can always be rebuilt from it.
public record StoredDocument(string ObjectKey, string FileName, RetrievalScope Scope);

public record IngestedDocument(StoredDocument Document, int ChunkCount);
