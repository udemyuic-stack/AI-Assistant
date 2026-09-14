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

    // A news site's "latest headlines" / "recommended for you" sidebar is
    // rendered as ordinary <p>/<li> tags too - tag-based filtering alone
    // can't tell it apart from the article body. What does tell them apart:
    // a headline list is nearly all link text (each item links to another
    // article); real prose has a handful of inline links in a paragraph of
    // otherwise plain text. Observed directly: a kun.uz scrape put two
    // sidebar blocks ("Medvedev Litvani...", "Real Barsaga yetib oldi...")
    // ahead of the article's own opening paragraph in the chunk ranking,
    // because they scored competitively on the embedding while contributing
    // nothing - the company name and route in that opening paragraph never
    // reached the model's context as a result.
    private const double MaxLinkTextRatio = 0.6;

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
            .Where(node => !IsLinkList(node))
            .Select(node => node.TextContent.Trim())
            .Where(text => text.Length > 0);

        var joined = string.Join("\n", blocks);
        return joined.Length > 0 ? joined : body.TextContent.Trim();
    }

    private static bool IsLinkList(IElement block)
    {
        var totalLength = block.TextContent.Trim().Length;
        if (totalLength == 0) return true;

        var linkLength = block.QuerySelectorAll("a").Sum(a => a.TextContent.Trim().Length);
        return (double)linkLength / totalLength > MaxLinkTextRatio;
    }

    // "kun.uz-indoneziyada-parom-halokatga-uchradi.txt", not the page's
    // internal database id or the bare host - a citation should tell the
    // reader which real page it came from. Shared by both ingestion paths
    // that scrape a URL (the admin Sites/Pages flow and a link pasted
    // straight into a chat question).
    public static string SourceFileName(string url)
    {
        var uri = new Uri(url);
        var slug = uri.AbsolutePath.Trim('/').Replace('/', '-');
        var name = slug.Length > 0 ? $"{uri.Host}-{slug}" : uri.Host;
        return (name.Length > 80 ? name[..80] : name) + ".txt";
    }
}

public record ScrapedPage(string Url, string Text);
