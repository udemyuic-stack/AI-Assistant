using System.Globalization;

namespace AiAssistant.Api.Services;

// Names of the settings an admin can tune live, without a redeploy. Kept as
// constants so a typo in a key string fails to compile instead of silently
// reading a fallback forever.
public static class SysParamKeys
{
    public const string RagTopK = "RAG_TOP_K";
    public const string RagSimilarityThreshold = "RAG_SIMILARITY_THRESHOLD";
    public const string RagTemperature = "RAG_TEMPERATURE";
    public const string RagRerankEnabled = "RAG_RERANK_ENABLED";
    public const string PublicRateLimitPerMinute = "PUBLIC_RATE_LIMIT_PER_MIN";

    public static readonly IReadOnlyList<string> All =
        [RagTopK, RagSimilarityThreshold, RagTemperature, RagRerankEnabled, PublicRateLimitPerMinute];

    // Validated at the point of writing, not at the point of reading: a bad
    // value stored here silently falls back to the appsettings default on
    // every subsequent read (SysParamCache swallows the parse failure), so
    // the admin would see their change "saved" and never take effect.
    //
    // Bounds are chosen to keep a mistyped value from degrading retrieval:
    // TopK above a few hundred floods the prompt, a threshold outside 0..1
    // is meaningless for cosine similarity, and a temperature above 1 turns
    // a grounded answer into invention.
    public static bool IsValidValue(string key, string value, out string reason)
    {
        reason = string.Empty;

        switch (key)
        {
            case RagTopK:
                return InRange(value, 1, 200, "1 va 200 orasidagi butun son", out reason);

            case PublicRateLimitPerMinute:
                return InRange(value, 1, 10_000, "1 va 10000 orasidagi butun son", out reason);

            case RagSimilarityThreshold:
                return InRange(value, 0d, 1d, "0.0 va 1.0 orasidagi son", out reason);

            case RagTemperature:
                return InRange(value, 0d, 1d, "0.0 va 1.0 orasidagi son", out reason);

            case RagRerankEnabled:
                if (bool.TryParse(value, out _)) return true;
                reason = $"'{key}' uchun qiymat 'true' yoki 'false' bo'lishi kerak.";
                return false;

            default:
                reason = $"Noma'lum sozlama: {key}";
                return false;
        }
    }

    private static bool InRange(string value, int min, int max, string expected, out string reason)
    {
        reason = string.Empty;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= min && parsed <= max)
            return true;

        reason = $"Qiymat {expected} bo'lishi kerak.";
        return false;
    }

    // InvariantCulture explicitly: these values are stored and read back as
    // technical strings, so "0.4" must not depend on the server's locale
    // deciding whether the separator is a dot or a comma.
    private static bool InRange(string value, double min, double max, string expected, out string reason)
    {
        reason = string.Empty;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= min && parsed <= max)
            return true;

        reason = $"Qiymat {expected} bo'lishi kerak.";
        return false;
    }
}
