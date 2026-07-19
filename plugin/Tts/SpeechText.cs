using System.Text.RegularExpressions;

namespace PopotoVox.Tts;

/// <summary>
/// Light, pronunciation-only text fixes applied just before synthesis.
///
/// The phonemizer (espeak-ng, used by both Piper and Kokoro) treats a word with
/// irregular capitalization as an initialism and spells it out letter by letter —
/// so an NPC who speaks in deliberate "rAnDoM cApS" (a real FFXIV character trait)
/// would be read "r-a-n-d-o-m". We detect those words and normalize their case so
/// they're spoken as words. This affects ONLY the audio text; the original (with
/// its quirky casing) still feeds the LLM and the bio card, where the trait matters.
/// </summary>
public static partial class SpeechText
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = FixErraticCase(text);
        text = FixPauseDashes(text);
        return text;
    }

    /// <summary>Strips any stray inline performance tags (e.g. &lt;sigh&gt;) so a non-performing engine never
    /// reads them aloud — defensive; the current engines emit delivery direction out-of-band, not in the text.</summary>
    public static string StripEmotionTags(string text) =>
        string.IsNullOrEmpty(text) ? text : EmotionTagPattern().Replace(text, " ").Replace("  ", " ").Trim();

    private static string FixErraticCase(string text) =>
        WordPattern().Replace(text, m => IsErraticCase(m.Value) ? m.Value.ToLowerInvariant() : m.Value);

    /// <summary>
    /// FFXIV uses em/en/box-drawing dashes and "--" to mark a pause, but espeak/Kokoro
    /// only pauses on a real sentence break (a period followed by a capitalized word —
    /// a period before a lowercase word is NOT paused). So we turn pause-dashes into a
    /// sentence break and capitalize the following word, which makes the pause audible.
    /// Single hyphens inside words (well-being, Mother-in-law) are left untouched.
    /// </summary>
    private static string FixPauseDashes(string text) =>
        PauseDash().Replace(text, m =>
        {
            var next = m.Groups[1].Value;
            return ". " + (next.Length > 0 ? char.ToUpperInvariant(next[0]).ToString() : string.Empty);
        });

    /// <summary>
    /// Erratic = mixes upper- and lower-case with an uppercase letter somewhere past
    /// the first character (e.g. "ComPllEEtLey", "WRroNg", "tHaNk"). Left untouched:
    /// lowercase words, Title-case words, ALL-CAPS acronyms, and single letters.
    /// </summary>
    private static bool IsErraticCase(string word)
    {
        bool hasUpper = false, hasLower = false, upperAfterFirst = false;
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            if (char.IsUpper(c))
            {
                hasUpper = true;
                if (i > 0) upperAfterFirst = true;
            }
            else if (char.IsLower(c))
            {
                hasLower = true;
            }
        }
        return hasUpper && hasLower && upperAfterFirst;
    }

    // A word is a run of letters, allowing internal apostrophes (don't, Y'shtola).
    [GeneratedRegex(@"[A-Za-z][A-Za-z']*")]
    private static partial Regex WordPattern();

    // A pause dash: em/en/horizontal-bar/box-drawing dashes, a run of 2+ hyphens, or a
    // single hyphen surrounded by whitespace — plus the first letter of the next word.
    [GeneratedRegex(@"\s*(?:[—–―─]+|-{2,}|(?<=\s)-(?=\s))\s*([A-Za-z])?")]
    private static partial Regex PauseDash();

    [GeneratedRegex(@"\s*<(?:sigh|laugh|chuckle|gasp|yawn|cough|sniffle|groan|breath|exhale)>\s*", RegexOptions.IgnoreCase)]
    private static partial Regex EmotionTagPattern();
}
