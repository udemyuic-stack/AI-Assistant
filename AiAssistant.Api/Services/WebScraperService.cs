using AngleSharp;
using AngleSharp.Dom;

namespace AiAssistant.Api.Services;

// Turns a URL into the text worth indexing. The interface is one method
// because that is the whole job from a caller's point of view; everything
// about which tags are noise lives on the far side of it.
public class WebScraperService(IHttpClientFactory factory, ILogger<WebScraperService> logger)
{
    // Tags that are never page content: navigation chrome, scripts, and
    // markup a browser wouldn't render as text either way.
    private static readonly string[] NoiseSelectors =
        ["script", "style", "nav", "header", "footer", "noscript", "svg", "iframe"];

    public async Task<ScrapedPage> ScrapeAsync(string url, CancellationToken ct = default)
    {
        var html = await FetchAsync(url, ct);
        var text = await ExtractTextAsync(html, url, ct);

        if (string.IsNullOrWhiteSpace(text))
            throw new WebScraperException($"'{url}' sahifasidan matn topilmadi.");

        return new ScrapedPage(url, text);
    }

    private async Task<string> FetchAsync(string url, CancellationToken ct)
    {
        var http = factory.CreateClient("scraper");

        try
        {
            return await http.GetStringAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new WebScraperException($"'{url}' sahifasiga ulanib bo'lmadi.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WebScraperException($"'{url}' javob bermadi (timeout).", ex);
        }
    }

    // Async, not GetAwaiter().GetResult(): blocking a thread pool thread here
    // meant every concurrent scrape held one hostage for the duration of
    // AngleSharp's parse - fine at one request at a time, a self-inflicted
    // bottleneck once ingestion runs more than one page concurrently.
    private async Task<string> ExtractTextAsync(string html, string url, CancellationToken ct)
    {
        IDocument document;
        try
        {
            var context = BrowsingContext.New(AngleSharp.Configuration.Default);
            document = await context.OpenAsync(req => req.Content(html), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HTML tahlil qilinmadi: {Url}", url);
            throw new WebScraperException($"'{url}' HTML'ini tahlil qilib bo'lmadi.", ex);
        }

        foreach (var selector in NoiseSelectors)
            foreach (var node in document.QuerySelectorAll(selector).ToList())
                node.Remove();

        var body = document.Body;
        if (body is null) return string.Empty;

        // AngleSharp's Text() concatenates every text node with no regard for
        // block boundaries - "TitleParagraph one" with nothing between them.
        // Reading block-level elements individually and joining with newlines
        // keeps paragraphs, list items, and headings apart the way a chunker
        // needs them to be.
        var blocks = body.QuerySelectorAll("p, li, h1, h2, h3, h4, h5, h6, td, th")
            .Select(node => node.TextContent.Trim())
            .Where(text => text.Length > 0);

        var joined = string.Join("\n", blocks);
        return joined.Length > 0 ? joined : body.TextContent.Trim();
    }
}

public record ScrapedPage(string Url, string Text);
