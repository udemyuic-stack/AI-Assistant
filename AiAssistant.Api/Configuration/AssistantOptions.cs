namespace AiAssistant.Api.Configuration;

public class AssistantOptions
{
    public const string SectionName = "Assistant";

    // The only thing that changes between domains (ticket support, legal
    // advisor, ...). Same binary, different prompt.
    public string SystemPrompt { get; set; } =
        "Siz — foydalanuvchiga faqat berilgan kontekst (hujjatlar va baza ma'lumotlari) " +
        "asosida javob beradigan yordamchisiz.";

    public int GuestQuestionLimit { get; set; } = 5;
}
