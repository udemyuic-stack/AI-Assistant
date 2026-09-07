namespace AiAssistant.Api.Models;

// Content arrives as a stream, not a path: writing an upload to a temp file
// and cleaning it up afterwards is the index's business, not the caller's.
public record IngestRequest(
    Stream Content,
    string FileName,
    RetrievalScope Scope,
    string? Language = null);
