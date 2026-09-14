namespace AiAssistant.Api.Services;

// Answers must come back in Latin-script Uzbek regardless of what the source
// documents look like. Two ways that goes wrong, and they are not the same
// failure:
//
//   1. The model answers in Russian - a different language entirely.
//   2. The model answers in Uzbek but in Cyrillic script, mirroring a Cyrillic
//      source. The words are right, the alphabet is not.
//
// The second was invisible here until a kun.uz /kr/ article was ingested and
// roughly two thirds of the answer came back as "Маъмура Бурҳонованинг куёв
// томони...". The old check looked only for letters unique to Russian, and
// Uzbek Cyrillic is not Russian, so nothing fired.
//
// These are cheap signals, not a classifier: they flag an answer for logging
// and evaluation rather than blocking it.
public static class UzbekLanguageGuard
{
    // Cyrillic letters used in Russian but not in the Uzbek Cyrillic alphabet.
    private static readonly char[] RussianOnlyLetters = ['ы', 'э', 'ё'];

    private const int MinimumLengthToJudge = 20;
    private const int SuspectLetterCount = 2;

    // Above this share of Cyrillic among the letters, the answer is not in the
    // alphabet it was asked for. Deliberately not zero: a correctly-cited
    // proper noun or a quoted phrase from a Cyrillic source is legitimate, and
    // flagging every one of those would make the signal useless.
    private const double CyrillicShareThreshold = 0.15;

    public static bool LooksLikeRussian(string text) =>
        text.Length >= MinimumLengthToJudge &&
        text.Count(RussianOnlyLetters.Contains) > SuspectLetterCount;

    // True when a meaningful part of the answer is in Cyrillic - Uzbek or
    // Russian, both wrong when Latin was required.
    public static bool LooksLikeCyrillic(string text)
    {
        if (text.Length < MinimumLengthToJudge) return false;

        var letters = 0;
        var cyrillic = 0;

        foreach (var character in text)
        {
            if (!char.IsLetter(character)) continue;
            letters++;
            if (character is >= 'Ѐ' and <= 'ӿ') cyrillic++;
        }

        return letters > 0 && (double)cyrillic / letters > CyrillicShareThreshold;
    }

    // What the pipeline actually asks: did the answer come back in the wrong
    // script or the wrong language, either way.
    public static bool LooksWrongScript(string text) =>
        LooksLikeCyrillic(text) || LooksLikeRussian(text);
}
