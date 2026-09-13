using System.Text.RegularExpressions;
using AiAssistant.Api.Models;

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

        try
        {
            var scraped = await scraper.ScrapeAsync(url, ct);
            await using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(scraped.Text));

            var fileName = new Uri(url).Host + ".txt";
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
}
