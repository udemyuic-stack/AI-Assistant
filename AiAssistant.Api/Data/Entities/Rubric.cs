namespace AiAssistant.Api.Data.Entities;

// A pre-written question a guest can tap instead of typing one - keeps an
// anonymous visitor's five-question budget from being spent on questions the
// index can't answer.
public class Rubric
{
    public int Id { get; set; }
    public required string QuestionText { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
