namespace AiAssistant.Api.Services;

// Source documents are often Russian while answers must be Uzbek, so the model
// can quietly answer in the wrong language. This is a cheap signal, not a
// classifier: it flags a suspect answer for logging and evaluation rather than
// blocking it.
public static class UzbekLanguageGuard
{
    // Cyrillic letters used in Russian but not in the Uzbek Cyrillic alphabet.
    private static readonly char[] RussianOnlyLetters = ['ы', 'э', 'ъ', 'ё'];

    private const int MinimumLengthToJudge = 20;
    private const int SuspectLetterCount = 2;

    public static bool LooksLikeRussian(string text) =>
        text.Length >= MinimumLengthToJudge &&
        text.Count(RussianOnlyLetters.Contains) > SuspectLetterCount;
}
