using AiAssistant.Api.Data;
using AiAssistant.Api.Models;
using AiAssistant.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this WebApplication app)
    {
        // Stores one upload and indexes it. ticketScope (optional form field)
        // keeps the document private to a single conversation instead of
        // adding it to the shared knowledge base.
        app.MapPost("/api/documents", async (
            HttpRequest http,
            DocumentLibrary library,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data kutilgan." });

            var form = await http.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Fayl topilmadi." });

            var ticketScope = form["ticketScope"].FirstOrDefault();
            var scope = string.IsNullOrWhiteSpace(ticketScope)
                ? RetrievalScope.Global
                : RetrievalScope.ForTicket(ticketScope);

            await using var content = file.OpenReadStream();
            var request = new IngestRequest(content, file.FileName, scope, form["language"].FirstOrDefault());

            try
            {
                var ingested = await library.AddAsync(request, progress: null, ct);
                return Results.Ok(ToResponse(ingested));
            }
            catch (OllamaUnavailableException ex)
            {
                logger.LogError(ex, "Vektorlash muvaffaqiyatsiz. Fayl={FileName}", file.FileName);
                return Results.Problem("Vektorlash xizmati mavjud emas.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (DocumentStorageException ex)
            {
                logger.LogError(ex, "Fayl saqlanmadi. Fayl={FileName}", file.FileName);
                return Results.Problem("Fayl ombori mavjud emas.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting("ollama");

        // What is actually in the knowledge base. The upload endpoint above
        // reports one file at a time and nothing reports the whole set, so
        // "did that PDF ever get indexed, and into how many chunks" had no
        // answer short of a psql session.
        //
        // Admin-only: a file name plus its chunk count is an inventory of the
        // corpus, including ticket-scoped uploads that belong to one
        // conversation. Listing those to anyone who can reach the port leaks
        // both what exists and, through the scope value, which ticket it
        // belongs to.
        app.MapGet("/api/documents", async (AiDbContext db, CancellationToken ct) =>
        {
            var documents = await db.ExtractedContents.AsNoTracking()
                .OrderByDescending(e => e.CreatedAt)
                .ThenByDescending(e => e.Id)
                // Bounded rather than open: this is an admin table with no
                // paging on the client, and an unbounded SELECT over a corpus
                // that only ever grows is a slow query waiting to happen.
                // Raise this together with paging, not on its own.
                .Take(500)
                .Select(e => new
                {
                    e.Id,
                    e.SourceFileName,

                    // Null for uploads, set for scrapes - the two ingestion
                    // paths name their source differently and a table that
                    // only showed SourceFileName would render every scraped
                    // page as a blank row.
                    SourceUrl = e.TargetPage != null ? e.TargetPage.Url : null,

                    e.StorageObjectKey,
                    e.Language,

                    // Correlated subqueries, one JOIN each in the emitted SQL -
                    // deliberately not a Count()/First() call per document in a
                    // loop, which is the N+1 this table would otherwise be.
                    ChunkCount = e.Chunks.Count,

                    // Scope lives on ContentChunk, not here: it is decided at
                    // indexing time, after extraction. Every chunk of one
                    // document shares it today, so the first is representative;
                    // null when the document was extracted but never indexed,
                    // which is itself the interesting case.
                    Scope = e.Chunks.Select(c => c.Scope).FirstOrDefault(),

                    e.CreatedAt
                })
                .ToListAsync(ct);

            return Results.Ok(documents);
        }).AddEndpointFilter<AdminKeyFilter>();
    }

    internal static object ToResponse(IngestedDocument ingested) => new
    {
        status = "ingested",
        file = ingested.Document.FileName,
        objectKey = ingested.Document.ObjectKey,
        scope = ingested.Document.Scope.Value,
        chunks = ingested.ChunkCount
    };
}
