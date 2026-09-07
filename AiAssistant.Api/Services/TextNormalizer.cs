using System.Text;
using System.Text.RegularExpressions;

namespace AiAssistant.Api.Services;

// Cleans raw extracted text before it's chunked. Scraped HTML and parsed PDFs
// both produce text with the same three problems: inconsistent Unicode forms,
// runs of whitespace from layout markup, and control characters that came
// along for the ride. None of this is chunking or embedding logic - it earns
// its own seam because "is this text clean" is a different question from "how
// do I split it".
public static partial class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // NFC: Uzbek and Russian Cyrillic can arrive as either precomposed
        // characters or a base letter plus a combining diacritic. Two chunks
        // of the same word in different forms would otherwise embed as
        // different vectors.
        var normalized = text.Normalize(NormalizationForm.FormC);

        normalized = ControlCharacters().Replace(normalized, " ");
        normalized = RepeatedBlankLines().Replace(normalized, "\n\n");
        normalized = TrailingLineWhitespace().Replace(normalized, "\n");

        return normalized.Trim();
    }

    // Everything below 0x20 except tab/newline, plus the Unicode replacement
    // character a bad encoding guess leaves behind.
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F�]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex RepeatedBlankLines();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingLineWhitespace();
}
