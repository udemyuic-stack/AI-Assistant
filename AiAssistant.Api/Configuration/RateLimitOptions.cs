namespace AiAssistant.Api.Configuration;

// Chat and ingestion both trigger an Ollama call - the expensive, easily
// exhausted resource in this system. Limiting is keyed on that, not on
// requests in general: a burst of /health checks should never count against
// the same budget as a burst of questions.
public class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int RequestsPerMinute { get; set; } = 10;
}
