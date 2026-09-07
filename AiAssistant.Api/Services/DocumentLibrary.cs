using AiAssistant.Api.Data;
using AiAssistant.Api.Data.Entities;
using AiAssistant.Api.Models;

namespace AiAssistant.Api.Services;

// Adding a document means three things: keeping the original, making it
// searchable, and recording what happened. All three happen behind one call,
// in that order - storage first so a failed embed still leaves a re-indexable
// file, index before the audit rows so a chunk row is never written for a
// vector that doesn't exist.
public class DocumentLibrary(
    DocumentStorage storage,
    DocumentIndex index,
    AiDbContext db,
    ILogger<DocumentLibrary> logger)
{
    public async Task<IngestedDocument> AddAsync(
        IngestRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // One local copy serves both steps: MinIO uploads from a path, and
        // PDF extraction needs random access that an upload stream lacks.
        var localCopy = await BufferToDiskAsync(request.Content, ct);

        try
        {
            progress?.Report("Fayl saqlanyapti...");
            var stored = await storage.UploadAsync(localCopy, request.FileName, request.Scope, ct);

            var chunks = await index.IndexFileAsync(localCopy, stored, request.Language, progress, ct);

            await RecordAsync(stored, request, chunks, ct);

            logger.LogInformation(
                "Hujjat qo'shildi. ObjectKey={ObjectKey}, Scope={Scope}, Chunks={ChunkCount}",
                stored.ObjectKey, stored.Scope.Value, chunks.Count);

            return new IngestedDocument(stored, chunks.Count);
        }
        finally
        {
            if (File.Exists(localCopy)) File.Delete(localCopy);
        }
    }

    // The Postgres side of ingestion: one ExtractedContent row for the source,
    // one ContentChunk per vector actually written. If this throws, the
    // vectors already in Qdrant are orphaned rather than lost - re-running
    // ingestion for the same source is safe (it re-embeds and re-audits), so
    // this is a gap to monitor, not a transaction to force across two
    // different stores.
    private async Task RecordAsync(
        StoredDocument stored,
        IngestRequest request,
        IReadOnlyList<IndexedChunk> chunks,
        CancellationToken ct)
    {
        var content = new ExtractedContent
        {
            TargetPageId = request.TargetPageId,
            SourceFileName = request.TargetPageId is null ? stored.FileName : null,
            StorageObjectKey = stored.ObjectKey,
            Text = string.Join("\n\n", chunks.Select(c => c.Text)),
            Language = request.Language
        };

        content.Chunks = chunks.Select(c => new ContentChunk
        {
            QdrantPointId = c.QdrantPointId,
            Text = c.Text,
            Scope = stored.Scope.Value,
            Language = request.Language
        }).ToList();

        db.ExtractedContents.Add(content);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<string> BufferToDiskAsync(Stream content, CancellationToken ct)
    {
        var path = Path.GetTempFileName();
        await using var file = new FileStream(path, FileMode.Create);
        await content.CopyToAsync(file, ct);
        return path;
    }
}
