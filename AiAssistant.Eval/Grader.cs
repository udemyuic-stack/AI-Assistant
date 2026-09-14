namespace AiAssistant.Eval;

// Deterministic grading, no judge model.
//
// An LLM judge would cost a second generation per question - on this host that
// turns a 37-question run from 40 minutes into 80 - and it brings its own
// error rate into the measurement, so a drop in the score stops being
// attributable to the change under test. Fact assertions are cheap, stable,
// and say exactly which fact went missing.
public static class Grader
{
    // Kept loose on purpose: the model may phrase a refusal slightly
    // differently ("ma'lumot topilmadi", "ma'lumot yo'q") and the gold set
    // should not fail over wording it never promised to control.
    private static readonly string[] RefusalMarkers =
        ["ma'lumot topilmadi", "maʼlumot topilmadi", "ma'lumot yo'q", "maʼlumot yoʻq"];

    // Retrieval pass = the chunk that holds the answer was actually fetched.
    // For a negative question the correct retrieval is no chunks at all: if
    // nothing scores above the threshold, the model is never given a chance
    // to invent something, and no source is cited under a refusal.
    public static ItemResult GradeRetrieval(GoldItem item, IReadOnlyList<RetrievedChunk> chunks)
    {
        if (item.KutilganManba is null)
        {
            var passed = chunks.Count == 0;
            return new ItemResult(item.Id, item.Toifa, passed,
                passed
                    ? "bo'sh (to'g'ri)"
                    : $"{chunks.Count} ta ortiqcha chunk: {Sources(chunks)}");
        }

        var found = chunks.Any(c => c.Source == item.KutilganManba);
        if (!found)
        {
            return new ItemResult(item.Id, item.Toifa, false,
                chunks.Count == 0
                    ? "hech narsa topilmadi"
                    : $"noto'g'ri manba: {Sources(chunks)}");
        }

        // Precision matters as much as recall here: a run where the right
        // document is present but four of eight slots hold something else is
        // how the Tashkent article ended up in a ferry answer's context.
        var relevant = chunks.Count(c => c.Source == item.KutilganManba);
        return new ItemResult(item.Id, item.Toifa, true,
            $"{relevant}/{chunks.Count} aniq, eng yaxshi ball {chunks.Max(c => c.Score):F4}");
    }

    public static ItemResult GradeAnswer(GoldItem item, string answer)
    {
        var declined = RefusalMarkers.Any(m =>
            answer.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (item.RadJavobKutiladi)
        {
            return new ItemResult(item.Id, item.Toifa, declined,
                declined ? "rad etdi (to'g'ri)" : $"to'qidi: {Trim(answer)}");
        }

        if (declined)
            return new ItemResult(item.Id, item.Toifa, false, "javob bermadi (rad etdi)");

        var missing = item.BolishiShart
            .Where(f => !answer.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var forbidden = item.BolmasligiShart
            .Where(f => answer.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count > 0 || forbidden.Count > 0)
        {
            var reason = missing.Count > 0 ? $"yetishmaydi: {string.Join(", ", missing)}" : "";
            if (forbidden.Count > 0)
                reason += (reason.Length > 0 ? "; " : "") + $"ortiqcha: {string.Join(", ", forbidden)}";
            return new ItemResult(item.Id, item.Toifa, false, reason);
        }

        return new ItemResult(item.Id, item.Toifa, true, "ok");
    }

    private static string Sources(IReadOnlyList<RetrievedChunk> chunks) =>
        string.Join(", ", chunks.Select(c => Shorten(c.Source)).Distinct());

    private static string Shorten(string source) =>
        source.Length > 34 ? source[..34] + "…" : source;

    private static string Trim(string text) =>
        text.Length > 70 ? text[..70].ReplaceLineEndings(" ") + "…" : text.ReplaceLineEndings(" ");
}
