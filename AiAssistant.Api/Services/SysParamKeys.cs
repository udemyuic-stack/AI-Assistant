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
}
