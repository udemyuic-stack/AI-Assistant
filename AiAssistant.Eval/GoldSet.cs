using System.Text.Json.Serialization;

namespace AiAssistant.Eval;

// The gold set as it sits on disk. Uzbek property names because the file is
// meant to be edited by the people who know the corpus, not only by whoever
// wrote this runner.
public record GoldSet(
    [property: JsonPropertyName("savollar")] IReadOnlyList<GoldItem> Savollar);

public record GoldItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("toifa")] string Toifa,
    [property: JsonPropertyName("savol")] string Savol,
    [property: JsonPropertyName("kutilgan_manba")] string? KutilganManba,
    [property: JsonPropertyName("bolishi_shart")] IReadOnlyList<string> BolishiShart,
    [property: JsonPropertyName("bolmasligi_shart")] IReadOnlyList<string> BolmasligiShart,
    [property: JsonPropertyName("rad_javob_kutiladi")] bool RadJavobKutiladi);

// One question's outcome. Saved to disk so a later run can diff against it -
// a pass rate that stayed at 84% while a different four questions broke is
// not "no change", and only a per-question comparison shows that.
public record ItemResult(
    string Id,
    string Toifa,
    bool Passed,
    string Detail);

public record RunResult(
    string Mode,
    DateTimeOffset RunAt,
    IReadOnlyList<ItemResult> Items);
