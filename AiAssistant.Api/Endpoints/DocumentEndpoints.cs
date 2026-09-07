using AiAssistant.Api.Models;
using AiAssistant.Api.Services;
using Microsoft.AspNetCore.RateLimiting;

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
