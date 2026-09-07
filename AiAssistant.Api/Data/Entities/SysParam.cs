namespace AiAssistant.Api.Data.Entities;

// Runtime-tunable settings (RAG_TOP_K, temperature, rate limit) - editable
// from the admin panel without a redeploy. IOptions<T> covers deploy-time
// config; this covers the subset an admin needs to change live. Read through
// SysParamCache, never this table directly, so a slider change doesn't cost a
// database round-trip on every question.
public class SysParam
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
