using System.Text.RegularExpressions;
using AiAssistant.Api.Data;
using AiAssistant.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AiAssistant.Api.Services;

// A question can carry its own source: "https://kun.uz/... shu sahifani
// o'qib javob ber". Without this, that URL just sits in the prompt as text -
// retrieval only ever searches what an admin already scraped ahead of time,
// so the model correctly says "not found" about a page it was never shown.
//
// Detected and ingested through the exact same path as an admin's Sites/Pages
// scrape (WebScraperService -> DocumentLibrary) - same SSRF guard, same
// chunking, same audit trail. The only difference is *when* it happens
// (inline, on this question) and *where* it lands (this asker's own scope,
// not the shared knowledge base an admin curated).
public partial class InlineLinkIngestor(
    WebScraperService scraper,
    DocumentLibrary library,
    AiDbContext db,
    ILogger<InlineLinkIngestor> logger)
{
    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();

    // Scrapes the first URL found in the question, if any, and ingests it
    // into the given scope. Silently does nothing on failure (a dead link,
    // a blocked private address) - the question still gets answered from
    // whatever the knowledge base already has, rather than failing outright
    // over a URL that turned out to be unreachable.
    public async Task IngestAnyLinkAsync(string question, RetrievalScope scope, CancellationToken ct)
    {
        var match = UrlPattern().Match(question);
        if (!match.Success) return;

        // Trailing punctuation ("...kun.uz/maqola." at a sentence's end) is
        // part of the sentence, not the URL.
        var url = match.Value.TrimEnd('.', ',', ')', ']', '!', '?');
        var fileName = WebScraperService.SourceFileName(url);

        // Asking a follow-up question about a link already pasted re-sent the
        // whole URL, and this method re-scraped, re-embedded, and re-indexed
        // it every time: the same kun.uz article was stored twice, 42 chunks
        // each, 18 minutes apart. Beyond the wasted fetch and embed, both
        // copies then compete in retrieval - identical text can take two of
        // the eight context slots and crowd out a chunk that would have said
        // something new.
        //
        // Checked before the scrape, not after, so a repeat costs one indexed
        // query rather than a full page fetch.
        //
        // Deliberately only here, not in the admin Sites/Pages scrape: an
        // admin pressing "scrape" is asking for a refresh, so skipping that
        // would break the one path whose entire purpose is to re-read a page.
        if (await AlreadyIngestedAsync(fileName, scope, ct))
        {
            logger.LogInformation(
                "Havola allaqachon o'qilgan, qayta yuklanmadi. Url={Url}, Scope={Scope}",
                url, scope.Value);
            return;
        }

        try
        {
            var scraped = await scraper.ScrapeAsync(url, ct);
            await using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(scraped.Text));

            var request = new IngestRequest(content, fileName, scope);

            await library.AddAsync(request, progress: null, ct);
        }
        catch (WebScraperException ex)
        {
            logger.LogWarning(ex, "Savoldagi havolani o'qib bo'lmadi: {Url}", url);
        }
        catch (OllamaUnavailableException ex)
        {
            logger.LogWarning(ex, "Savoldagi havolani vektorlab bo'lmadi: {Url}", url);
        }
    }

    // SourceFileName is a pure function of the URL, so it doubles as the key
    // for "have we read this page into this scope already" without a URL
    // column and the migration one would cost. Scope is carried on the chunks
    // rather than the parent row, which is why the match reaches through them.
    //
    // Known limitation: a page whose content later changes is never re-read
    // into a scope that already has it. Detecting that needs a content hash
    // stored per document and a freshness window - worth doing, but a
    // different change from stopping the same page being stored twice in the
    // same session.
    private Task<bool> AlreadyIngestedAsync(string fileName, RetrievalScope scope, CancellationToken ct) =>
        db.ExtractedContents
            .AsNoTracking()
            .AnyAsync(e => e.SourceFileName == fileName
                           && e.Chunks.Any(c => c.Scope == scope.Value), ct);
}
