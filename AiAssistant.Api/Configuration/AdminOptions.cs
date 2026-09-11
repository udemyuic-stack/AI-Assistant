namespace AiAssistant.Api.Configuration;

public class AdminOptions
{
    public const string SectionName = "Admin";

    // Never committed - supplied via environment or a secrets store, the same
    // way the MinIO credentials are. Empty means admin endpoints refuse every
    // request rather than allowing them.
    public string ApiKey { get; set; } = string.Empty;
}
