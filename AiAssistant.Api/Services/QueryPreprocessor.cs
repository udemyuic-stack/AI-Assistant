using AiAssistant.Api.Configuration;
using Microsoft.Extensions.Options;

namespace AiAssistant.Api.Services;

// The PRE-RETRIEVAL stage: everything that happens to a question between the
// user typing it and the vector search running.
//
// The problem it solves is named in the book's own limitations: a corpus
// written across Uzbek Latin, Uzbek Cyrillic, and Russian, queried by users
// who each think in one of them. Vector similarity does not reliably bridge
// "железка" to "temir yo'l", and the stem fallback in DocumentIndex only
// catches inflections of a word already present - never its translation.
//
// Currently implements: slang normalisation (deterministic, always on) and
// query expansion (one model call, off by default). Query rewriting from
// conversation history and HyDE are not implemented - see the tasks for why
// each is deferred rather than missing.
public class QueryPreprocessor(
    OllamaClient ollama,
    SlangDictionary slang,
    IOptions<RagOptions> options,
    ILogger<QueryPreprocessor> logger)
{
    private readonly RagOptions _options = options.Value;

    private const string ExpansionInstruction =
        "Quyidagi savol uchun qidiruv uchun 3 ta muqobil ifoda yozing: " +
        "biri o'zbek lotin, biri o'zbek kirill, biri rus tilida. " +
        "Faqat ifodalarni yozing, har birini yangi qatorda, izohsiz va raqamsiz.";

    private const string HydeInstruction =
        "Quyidagi savolga rasmiy hujjat uslubida qisqa (2-3 jumla) faraziy javob yozing. " +
        "Aniq bilmasangiz ham, shunday hujjat qanday yozilishi mumkinligini taxmin qiling. " +
        "Faqat javob matnini yozing.";

    // Returns every phrasing worth searching. The user's own words - after
    // slang normalisation - are always first: an expansion that goes wrong
    // must never replace what they actually asked.
    public async Task<IReadOnlyList<string>> PrepareAsync(string question, CancellationToken ct = default)
    {
        var normalized = slang.Normalize(question);
        var phrasings = new List<string> { normalized };

        if (_options.QueryExpansionEnabled)
            phrasings.AddRange(await AskModelAsync(ExpansionInstruction, normalized, take: 3, ct));

        // HyDE contributes one more "phrasing": a hypothetical answer, which
        // is worded like the passage being looked for rather than like the
        // question. Multi-query search treats it as just another vector.
        if (_options.HydeEnabled)
            phrasings.AddRange(await AskModelAsync(HydeInstruction, normalized, take: 1, ct));

        return phrasings.Distinct().ToList();
    }

    private async Task<IReadOnlyList<string>> AskModelAsync(
        string instruction, string question, int take, CancellationToken ct)
    {
        try
        {
            var messages = new[] { ("system", instruction), ("user", question) };

            var response = new System.Text.StringBuilder();
            await foreach (var token in ollama.StreamChatAsync(messages, temperature: 0, ct))
                response.Append(token);

            // Multi-line for expansion (one phrasing per line), single block
            // for HyDE (a short passage that must stay intact).
            if (take == 1)
            {
                var passage = response.ToString().Trim();
                return passage.Length > 2 ? [passage] : [];
            }

            return response.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length is > 2 and < 200)
                .Take(take)
                .ToList();
        }
        catch (OllamaUnavailableException ex)
        {
            // Pre-retrieval is an optimisation. If it fails, searching the
            // original question is still correct - just narrower.
            logger.LogWarning(ex, "Savolni tayyorlab bo'lmadi, asl savol ishlatiladi.");
            return [];
        }
    }
}
