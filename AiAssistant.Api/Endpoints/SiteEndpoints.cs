using AiAssistant.Api.Data;
using AiAssistant.Api.Data.Entities;
using AiAssistant.Api.Models;
using AiAssistant.Api.Services;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Endpoints;

// Sites/pages are plain records - CRUD stays a thin pass-through to
// AiDbContext here rather than behind a service, because a service with no
// behaviour beyond "call SaveChanges" is a Middle Man. Scraping is real
// behaviour (fetch, extract, ingest) and gets its own case below.
public static class SiteEndpoints
{
    public static void MapSiteEndpoints(this WebApplication app)
    {
        app.MapPost("/api/sites", async (CreateSiteRequest request, AiDbContext db, CancellationToken ct) =>
        {
            var site = new Site { Name = request.Name, BaseUrl = request.BaseUrl };
            db.Sites.Add(site);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/sites/{site.Id}", site);
        });

        app.MapGet("/api/sites", async (AiDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Sites.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct)));

        app.MapPost("/api/sites/{siteId:int}/pages", async (
            int siteId, CreatePageRequest request, AiDbContext db, CancellationToken ct) =>
        {
            var siteExists = await db.Sites.AnyAsync(s => s.Id == siteId, ct);
            if (!siteExists)
                return Results.NotFound(new { error = $"Sayt {siteId} topilmadi." });

            var page = new TargetPage { SiteId = siteId, Url = request.Url };
            db.TargetPages.Add(page);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/pages/{page.Id}", page);
        });

        app.MapGet("/api/sites/{siteId:int}/pages", async (int siteId, AiDbContext db, CancellationToken ct) =>
            Results.Ok(await db.TargetPages.AsNoTracking()
                .Where(p => p.SiteId == siteId && p.IsActive)
                .ToListAsync(ct)));

        // Fetches the page, extracts its text, and runs it through the same
        // ingestion path as an upload (DocumentLibrary) - a scrape and an
        // upload differ only in where the bytes came from.
        app.MapPost("/api/pages/{pageId:int}/scrape", async (
            int pageId,
            WebScraperService scraper,
            DocumentLibrary library,
            AiDbContext db,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var page = await db.TargetPages.FindAsync([pageId], ct);
            if (page is null)
                return Results.NotFound(new { error = $"Sahifa {pageId} topilmadi." });
            if (string.IsNullOrWhiteSpace(page.Url))
                return Results.BadRequest(new { error = "Sahifada URL yo'q." });

            try
            {
                var scraped = await scraper.ScrapeAsync(page.Url, ct);
                await using var content = ToStream(scraped.Text);

                var request = new IngestRequest(
                    content,
                    FileName: $"page-{pageId}.txt",
                    Scope: RetrievalScope.Global,
                    TargetPageId: pageId);

                var ingested = await library.AddAsync(request, progress: null, ct);
                return Results.Ok(DocumentEndpoints.ToResponse(ingested));
            }
            catch (WebScraperException ex)
            {
                logger.LogWarning(ex, "Scrape muvaffaqiyatsiz. PageId={PageId}", pageId);
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
            catch (OllamaUnavailableException ex)
            {
                logger.LogError(ex, "Vektorlash muvaffaqiyatsiz. PageId={PageId}", pageId);
                return Results.Problem("Vektorlash xizmati mavjud emas.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting("ollama");
    }

    private static MemoryStream ToStream(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));
}

public record CreateSiteRequest(string Name, string? BaseUrl);
public record CreatePageRequest(string Url);
