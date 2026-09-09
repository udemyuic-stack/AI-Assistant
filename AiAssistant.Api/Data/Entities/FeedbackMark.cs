namespace AiAssistant.Api.Data.Entities;

// One feedback per question, one row - a rating a user changes their mind
// about updates this row rather than accumulating duplicates. Ownership
// (UserId matches the question's asker) is enforced at the service layer,
// not here: this is a data shape, not a policy.
public class FeedbackMark
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public Question Question { get; set; } = null!;

    public required string UserId { get; set; }
    public required string Code { get; set; } // green | red
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
