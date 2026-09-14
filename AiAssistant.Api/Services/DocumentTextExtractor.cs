using System.Text.RegularExpressions;
using AiAssistant.Api.Configuration;
using NPOI.XWPF.UserModel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace AiAssistant.Api.Services;

// Turns a file into overlapping text chunks. Kept apart from the index because
// the two answer different questions - "what does this document say" versus
// "where do its vectors live" - and because web scraping will arrive as a
// second source (it becomes a real seam then, not before).
public partial class DocumentTextExtractor(RagOptions options)
{
    public IReadOnlyList<string> ExtractChunks(string filePath, string fileName)
    {
        var text = TextNormalizer.Normalize(ReadText(filePath, fileName));
        return SplitIntoChunks(text);
    }

    private static string ReadText(string filePath, string fileName)
    {
        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return ReadPdf(filePath);
        if (fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            return ReadDocx(filePath);

        return File.ReadAllText(filePath);
    }

    // Legacy .doc (the pre-2007 binary format, not .docx) is deliberately not
    // handled here. NPOI reads it through a different API (HWPFDocument, a
    // separate binary-OLE2 parser with its own failure modes) and nothing has
    // required it yet - every file this has actually been fed is either PDF
    // or modern .docx. Add it if a real .doc upload shows up rather than
    // guessing its extraction quality now.
    //
    // Paragraphs become one "\n"-joined unit each, same convention as PDF
    // blocks and the HTML scraper's block elements: SentenceSplitter expects
    // a newline to mean "a real paragraph/heading/list-item boundary", not
    // just a place a sentence happens to end. Losing that here would mean a
    // heading and the paragraph after it get sentence-split as if they were
    // one continuous thought, the same defect page.Text glued into every PDF
    // page before ReadPdf was rewritten.
    private static string ReadDocx(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var document = new XWPFDocument(stream);

        var paragraphs = document.Paragraphs
            .Select(p => p.Text.Trim())
            .Where(t => t.Length > 0);

        // Tables carry real content (a law's fee schedule, a comparison
        // table) that Paragraphs does not include - XWPFDocument exposes
        // body paragraphs and body tables as separate collections, not one
        // reading-ordered stream. Appended after the paragraphs rather than
        // interleaved in true document order, which NPOI's object model does
        // not expose without walking the raw body elements; row-by-row is
        // still retrievable and searchable, just not positioned where the
        // table originally sat in the document.
        var tableRows = document.Tables.SelectMany(table => table.Rows)
            .Select(row => string.Join(" | ", row.GetTableCells().Select(c => c.GetText().Trim())))
            .Where(t => t.Length > 0);

        return string.Join("\n", paragraphs.Concat(tableRows));
    }

    // Page.Text (PdfPig's simple accessor) does two things wrong for the
    // documents this has actually been fed: some PDFs encode no explicit
    // space character at all and rely purely on glyph position to imply one,
    // which Page.Text does not reconstruct - confirmed on a real upload where
    // it returned "JasonBrownleeMasterMachineLearningAlgorithms..." for an
    // entire page, unusable by anything downstream. And even when spacing is
    // fine, it has no concept of a paragraph or a running header - a title
    // like "OpenCV-Python" plus a page number was found glued directly onto
    // the first real sentence of every single page, because there is no
    // punctuation between them for SentenceSplitter to cut on.
    //
    // NearestNeighbourWordExtractor rebuilds words from each letter's own
    // bounding box instead of trusting the content stream's spacing, which
    // fixes the first problem. DocstrumBoundingBoxes then groups those words
    // into blocks by geometry - paragraphs, headings, list items each land in
    // their own block, and a running header/page number reliably ends up
    // separated from the body text because it sits nowhere near it on the
    // page. That separation is what makes StripBoilerplate below simple: a
    // repeating header does not need to be pattern-matched out of a paragraph
    // it now never touches.
    private static string ReadPdf(string filePath)
    {
        using var pdf = PdfDocument.Open(filePath);
        var pages = pdf.GetPages().ToList();

        var pageBlocks = pages
            .Select(page =>
            {
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters);
                return DocstrumBoundingBoxes.Instance.GetBlocks(words)
                    .Select(b => b.Text.Trim())
                    .Where(t => t.Length > 0)
                    .ToList();
            })
            .ToList();

        // A scanned page has no letters at all - PdfPig reads the image fine
        // but there is no text layer under it to extract, so every stage
        // above runs without error and quietly returns nothing. A law book
        // that is a photocopy would otherwise ingest as an empty document:
        // "0 bo'lak saqlandi" with no indication that the content was never
        // readable in the first place, rather than the missing OCR step this
        // actually is. Checked as an average over the whole document, not
        // per page, so a PDF with a handful of legitimately blank or
        // image-only pages (a cover, a diagram) does not trip this.
        var totalChars = pageBlocks.Sum(blocks => blocks.Sum(b => b.Length));
        if (pages.Count > 0 && totalChars / (double)pages.Count < MinCharsPerPageBeforeOcrSuspected)
            throw new DocumentExtractionException(
                $"'{Path.GetFileName(filePath)}' fayldan deyarli matn chiqmadi " +
                $"({pages.Count} sahifa, jami {totalChars} belgi) - bu skanerlangan " +
                "(rasm sifatidagi) hujjat bo'lishi mumkin. Bunday fayllar uchun OCR kerak, " +
                "hozircha qo'llab-quvvatlanmaydi.");

        var keep = StripBoilerplate(pageBlocks);
        return string.Join("\n", keep);
    }

    // Chosen well below any real prose page (even a sparse one runs several
    // hundred characters) so a document that is mostly legitimate text with a
    // few blank or diagram-only pages never trips this by averaging down.
    private const double MinCharsPerPageBeforeOcrSuspected = 20;

    // Two independent noise patterns, both found on real uploads rather than
    // assumed:
    //
    //   - A block that is nothing but a number or roman numeral is a page
    //     number. It carries no meaning on its own regardless of how often it
    //     repeats, so it is dropped unconditionally rather than by frequency.
    //   - A block whose exact text recurs on most pages is a running header,
    //     footer, or title line ("OpenCV-Python" on every one of 88 pages).
    //     Requiring a majority, not just "more than once", keeps a section
    //     heading that happens to repeat two or three times in a long
    //     document (a repeated worked example, a recurring table header)
    //     from being mistaken for boilerplate.
    //
    // Same shape as WebScraperService's link-density filter: a statistical
    // property of the document decides what is noise, not an assumption about
    // this specific file's layout.
    private static List<string> StripBoilerplate(List<List<string>> pageBlocks)
    {
        var totalPages = pageBlocks.Count;
        if (totalPages < 4)
            return pageBlocks.SelectMany(blocks => blocks).ToList();

        var pageCountByBlock = pageBlocks
            .SelectMany(blocks => blocks.Distinct())
            .GroupBy(text => text)
            .ToDictionary(g => g.Key, g => g.Count());

        const double majorityThreshold = 0.6;
        var minPagesToCount = (int)Math.Ceiling(totalPages * majorityThreshold);

        return pageBlocks
            .SelectMany(blocks => blocks)
            .Where(block => !NumberOrRomanNumeral().IsMatch(block))
            .Where(block => pageCountByBlock[block] < minPagesToCount)
            .ToList();
    }

    [GeneratedRegex(@"^[0-9]+$|^[ivxlcdmIVXLCDM]+$")]
    private static partial Regex NumberOrRomanNumeral();

    // Packs whole sentences up to ChunkSize instead of cutting every ChunkSize
    // characters regardless of where that lands.
    //
    // The old fixed-width cut sliced mid-sentence and mid-number by design:
    // "Toshkent aholisi 3 212" could end one chunk and "200 kishi" begin the
    // next, and neither half answers the question the whole sentence answers.
    // Overlap papered over some of that, at the cost of storing a fifth of the
    // corpus twice.
    //
    // Overlap is kept, but measured in sentences rather than characters - the
    // last sentence of a chunk opens the next one, so a fact that spans a
    // boundary survives in at least one chunk intact.
    public IReadOnlyList<string> SplitIntoChunks(string text)
    {
        var size = options.ChunkSize;
        if (options.ChunkOverlap >= size)
            throw new InvalidOperationException(
                $"ChunkOverlap ({options.ChunkOverlap}) ChunkSize ({size}) dan kichik bo'lishi shart.");

        var sentences = SentenceSplitter.Split(text);
        if (sentences.Count == 0) return [];

        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        foreach (var sentence in sentences)
        {
            // A single sentence longer than the budget has no safe cut inside
            // it, so it becomes its own chunk rather than being wedged in next
            // to others and pushing them out. Rare in prose, common in the
            // run-on table rows a Wikipedia infobox produces.
            if (sentence.Length >= size)
            {
                Flush(chunks, current, ref currentLength);
                chunks.Add(sentence);
                continue;
            }

            if (currentLength + sentence.Length + 1 > size)
            {
                var carried = CarryOver(current, options.ChunkOverlap);
                Flush(chunks, current, ref currentLength);

                current.AddRange(carried);
                currentLength = carried.Sum(s => s.Length + 1);
            }

            current.Add(sentence);
            currentLength += sentence.Length + 1;
        }

        Flush(chunks, current, ref currentLength);

        // Same floor as before: a fragment this short carries no retrievable
        // meaning and only dilutes the ranking.
        return chunks.Where(c => c.Length > 50).ToList();
    }

    private static void Flush(List<string> chunks, List<string> current, ref int currentLength)
    {
        if (current.Count > 0) chunks.Add(string.Join(" ", current));
        current.Clear();
        currentLength = 0;
    }

    // The trailing sentences that fit inside the overlap budget, oldest last
    // restored to reading order.
    private static List<string> CarryOver(List<string> current, int overlapBudget)
    {
        var carried = new List<string>();
        var length = 0;

        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (length + current[i].Length > overlapBudget) break;
            carried.Insert(0, current[i]);
            length += current[i].Length + 1;
        }

        return carried;
    }
}

// Same shape as DocumentStorageException: a named failure for one stage of
// the ingest pipeline, so the endpoint can map it to a clean 4xx instead of
// an unhandled 500, and log something more specific than "an exception was
// thrown somewhere in AddAsync".
public class DocumentExtractionException(string message, Exception? inner = null)
    : Exception(message, inner);
