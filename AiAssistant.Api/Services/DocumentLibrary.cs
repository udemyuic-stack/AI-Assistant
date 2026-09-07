using AiAssistant.Api.Models;

namespace AiAssistant.Api.Services;

// Adding a document means two things: keeping the original and making it
// searchable. Both happen behind one call, in that order - storage first, so a
// failure while embedding leaves a file that can be re-indexed rather than an
// upload that was lost.
public class DocumentLibrary(
    DocumentStorage storage,
    DocumentIndex index,
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

            var chunkCount = await index.IndexFileAsync(
                localCopy, stored, request.Language, progress, ct);

            logger.LogInformation(
                "Hujjat qo'shildi. ObjectKey={ObjectKey}, Scope={Scope}, Chunks={ChunkCount}",
                stored.ObjectKey, stored.Scope.Value, chunkCount);

            return new IngestedDocument(stored, chunkCount);
        }
        finally
        {
            if (File.Exists(localCopy)) File.Delete(localCopy);
        }
    }

    private static async Task<string> BufferToDiskAsync(Stream content, CancellationToken ct)
    {
        var path = Path.GetTempFileName();
        await using var file = new FileStream(path, FileMode.Create);
        await content.CopyToAsync(file, ct);
        return path;
    }
}
