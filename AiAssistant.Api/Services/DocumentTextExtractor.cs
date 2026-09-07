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
        var text = ReadText(filePath, fileName);
        return SplitIntoChunks(text);
    }

    private static string ReadText(string filePath, string fileName)
    {
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(filePath);

        using var pdf = PdfDocument.Open(filePath);
        return string.Join("\n", pdf.GetPages().Select(page => page.Text));
    }

    public IReadOnlyList<string> SplitIntoChunks(string text)
    {
        var size = options.ChunkSize;
        var stride = size - options.ChunkOverlap;
        if (stride <= 0)
            throw new InvalidOperationException(
                $"ChunkOverlap ({options.ChunkOverlap}) ChunkSize ({size}) dan kichik bo'lishi shart.");

        var chunks = new List<string>();
        for (var start = 0; start < text.Length; start += stride)
        {
            var chunk = text.Substring(start, Math.Min(size, text.Length - start)).Trim();
            if (chunk.Length > 50)
                chunks.Add(chunk);
        }

        return chunks;
    }
}
