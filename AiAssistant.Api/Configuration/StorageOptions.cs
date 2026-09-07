namespace AiAssistant.Api.Configuration;

// Original uploads live in MinIO so a document can be re-indexed, re-read, or
// handed back to the user after the vectors have been rebuilt. Qdrant holds
// derived data only; MinIO holds the source of truth.
public class StorageOptions
{
    public const string SectionName = "Storage";

    public string Endpoint { get; set; } = "localhost:9100";
    public string Bucket { get; set; } = "ai-assistant-documents";
    public bool UseSsl { get; set; }

    // Never committed. Supplied via environment or a secrets store.
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
}
