using AiAssistant.Api.Configuration;
using UglyToad.PdfPig;

namespace AiAssistant.Api.Services;

// Turns a file into overlapping text chunks. Kept apart from the index because
// the two answer different questions - "what does this document say" versus
// "where do its vectors live" - and because web scraping will arrive as a
// second source (it becomes a real seam then, not before).
public class DocumentTextExtractor(RagOptions options)
{
    public IReadOnlyList<string> ExtractChunks(string filePath, string fileName)
    {
        var text = TextNormalizer.Normalize(ReadText(filePath, fileName));
        return SplitIntoChunks(text);
    }

    private static string ReadText(string filePath, string fileName)
    {
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(filePath);

        using var pdf = PdfDocument.Open(filePath);
        return string.Join("\n", pdf.GetPages().Select(page => page.Text));
    }

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
