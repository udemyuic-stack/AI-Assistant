using System.Text.RegularExpressions;

namespace AiAssistant.Api.Configuration;

public partial class OllamaOptions
{
    public const string SectionName = "Ollama";

    public string Host { get; set; } = "http://localhost:11434";

    // These defaults are reachable: appsettings.json is an optional provider,
    // so a launch from the wrong working directory silently falls back here.
    // They named qwen3:8b and qwen3-embedding:8b long after both were removed
    // from the host - a fallback pointing at a model that does not exist fails
    // loudly on the first call, which is the wrong failure to discover in
    // production but the right one to have caught here.
    public string ChatModel { get; set; } = "gemma4";
    public string EmbedModel { get; set; } = "bge-m3";
    public double TimeoutMinutes { get; set; } = 10;

    // Ollama defaults gemma4 to a large context window; its KV cache alone
    // costs several GB of RAM at that size. 8192 is enough for a RAG prompt
    // (question + retrieved chunks) without paying for context nothing uses.
    public int ChatContextTokens { get; set; } = 8192;

    // How long Ollama holds each model in RAM after its last call. The value
    // is parsed by Go's time.ParseDuration on the server, so a unit is not
    // optional: "10m" and "-1m" (indefinitely) are accepted, a bare "-1" is
    // rejected with 400 `time: missing unit in duration "-1"`. Checked against
    // a live /api/embed rather than taken from the docs, which show the bare
    // integer form - that form is only valid as a JSON number, not a string.
    //
    // Deliberately two settings, not one, because the two models have opposite
    // cost profiles on a RAM-constrained host:
    //
    //   bge-m3  - 1.2 GB, called on every question AND once per batch of 32
    //             chunks during ingestion. Cheap to hold, expensive to reload.
    //   gemma4  - 9.6 GB, called once per question. Holding it pins a third of
    //             this box's usable RAM against Rider, Chrome, and Postgres.
    //
    // The failure this avoids is not a slow reload - it is swap. A model paged
    // out to swap comes back through random 4K faults, which is slower than
    // reloading it from the page cache outright. Measured here: a cold embed
    // took 3.45s against 0.30s warm, an 11x gap, while both models were
    // resident and the box was 6.9 GiB into swap.
    //
    // If this ever runs on a host with real headroom (64 GB+), "-1" for both
    // is correct and costs nothing: resident weights are a cache, not waste.
    public string ChatKeepAlive { get; set; } = "2m";
    public string EmbedKeepAlive { get; set; } = "-1m";

    // Go's duration grammar, enforced here so a typo fails at startup instead
    // of turning every question into "Model hozir mavjud emas" with the real
    // 400 buried in the log - which is exactly how the bare "-1" above was
    // found.
    [GeneratedRegex(@"^-?(\d+(\.\d+)?(ns|us|µs|ms|s|m|h))+$")]
    private static partial Regex DurationPattern();

    public void Validate()
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(ChatKeepAlive), ChatKeepAlive),
                     (nameof(EmbedKeepAlive), EmbedKeepAlive)
                 })
        {
            if (!DurationPattern().IsMatch(value))
                throw new InvalidOperationException(
                    $"Ollama:{name} = '{value}' - noto'g'ri davomiylik. Birlik shart: " +
                    $"'30s', '10m', '-1m' (cheksiz). Yalang'och '-1' qabul qilinmaydi.");
        }
    }
}
