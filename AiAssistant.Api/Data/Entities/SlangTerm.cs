namespace AiAssistant.Api.Data.Entities;

// A colloquial or cross-script term and the wording official documents
// actually use ("железка" -> "temir yo'l"). Lives in the database because the
// entries worth having are discovered from real questions that returned
// nothing - an admin adding one shouldn't need a deploy.
public class SlangTerm
{
    public int Id { get; set; }
    public required string Term { get; set; }
    public required string CanonicalForm { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
