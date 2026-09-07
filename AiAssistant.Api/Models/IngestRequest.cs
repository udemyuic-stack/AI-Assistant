namespace AiAssistant.Api.Models;

// Content arrives as a stream, not a path: writing an upload to a temp file
// and cleaning it up afterwards is the index's business, not the caller's.
// TargetPageId is set only when the content came from a scrape, so the audit
// trail (ExtractedContent) can point back at the page it was pulled from.
public record IngestRequest(
    Stream Content,
    string FileName,
    RetrievalScope Scope,
    string? Language = null,
    int? TargetPageId = null);
