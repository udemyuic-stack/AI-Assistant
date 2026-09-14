using System.Text.RegularExpressions;

namespace AiAssistant.Api.Services;

// Finds where one sentence ends and the next begins, well enough to chunk on.
//
// Not a general NLP sentence tokenizer, and it does not need to be: the only
// question asked of it is "is this a safe place to cut", and being wrong in
// the conservative direction (refusing to cut) merely produces a slightly
// longer chunk, while being wrong the other way splits a fact in half.
//
// The cases that actually appear in this corpus and would break a naive split
// on '.':
//
//   "631.29 km2"                   decimal number
//   "M. P. Esholt (1657)"          initials
//   "XVIII asr va XIX asr..."      no period at all between clauses
//   "3 212 200 kishi"              digit groups
//
// Uzbek Cyrillic and Latin both end sentences with . ! ? and the same rules
// apply to each, so nothing here is script-specific.
public static partial class SentenceSplitter
{
    // A boundary is terminal punctuation followed by whitespace and something
    // that starts a new sentence. The lookbehind rejects the two false
    // positives above: a digit before the period (decimals) and a lone capital
    // letter before it (initials).
    [GeneratedRegex(@"(?<=[.!?])(?<!\d[.])(?<![ (][A-ZА-ЯЎҚҒҲ][.])\s+(?=[^\s])")]
    private static partial Regex BoundaryPattern();

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Newlines first. The scraper joins block-level elements with '\n', so
        // a newline here is a real paragraph, heading, or list-item boundary -
        // a stronger and more reliable signal than any punctuation heuristic,
        // and free.
        var units = new List<string>();

        foreach (var block in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = block.Trim();
            if (trimmed.Length == 0) continue;

            units.AddRange(BoundaryPattern().Split(trimmed)
                .Select(sentence => sentence.Trim())
                .Where(sentence => sentence.Length > 0));
        }

        return units;
    }
}
