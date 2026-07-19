namespace PopotoVox.Tts;

/// <summary>
/// The voice's gender bucket. Decided from reliable game data (Lumina
/// <c>ENpcBase.Gender</c>), never guessed by the LLM. <see cref="Neutral"/> means
/// "unknown / ambiguous" — selection then draws from the whole palette (PRD §5.7).
/// </summary>
public enum VoiceGender
{
    Neutral = 0,
    Male = 1,
    Female = 2,
}

/// <summary>Coarse age band a voice should read as. The LLM may suggest one; the matcher scores against it.</summary>
public enum VoiceAge
{
    Child = 0,
    Young = 1,
    Adult = 2,
    MiddleAged = 3,
    Elderly = 4,
}

/// <summary>
/// The real-world accent a voice reads as when speaking English. The Casting AI picks one
/// to fit an NPC's homeland/culture (so FFXIV regions sound distinct); the matcher prefers
/// a voice of that accent but never breaks the gender gate to get it. <see cref="Unknown"/>
/// means "no preference" — the matcher then draws from the whole gender pool. The set is
/// what our accent-rich model (Kokoro v1_0) actually ships; engines without a given accent
/// fall back to whatever they have (Piper is American-only).
/// </summary>
public enum VoiceAccent
{
    Unknown = 0,
    American,
    British,
    French,
    Italian,
    Spanish,
    Hindi,
    Japanese,
    Portuguese,
    Chinese,
    // Expanded for VoxCPM2 (Ultra), which voices these via a native-tongue reference line. Kokoro/Piper
    // ship no dedicated voices for most of these, so the matcher falls back to the general gender pool.
    German,
    Russian,
    Arabic,
    Korean,
    Thai,
    Vietnamese,
    Turkish,
    Scottish,
    Irish,
    Australian,
}

/// <summary>
/// The character-voice traits the casting LLM produces (engine-agnostic), which the
/// <see cref="PopotoVox.Casting.VoiceMatcher"/> maps onto a concrete voice from the
/// active engine's palette. Stored on the locked <see cref="VoiceSpec"/> so the same
/// intent re-maps cleanly if the engine changes.
/// </summary>
public sealed record VoiceTraits(VoiceAge Age, string Timbre, VoiceAccent Accent)
{
    public static readonly VoiceTraits Default = new(VoiceAge.Adult, "", VoiceAccent.Unknown);
}

public static class VoiceGenderExtensions
{
    /// <summary>Map the NPC record's gender string ("Male"/"Female") to the voice bucket.</summary>
    public static VoiceGender ToVoiceGender(this string? gender) => gender?.ToLowerInvariant() switch
    {
        "male" => VoiceGender.Male,
        "female" => VoiceGender.Female,
        _ => VoiceGender.Neutral,
    };

    public static VoiceAge ToVoiceAge(this string? age) => age?.ToLowerInvariant() switch
    {
        "child" => VoiceAge.Child,
        "young" => VoiceAge.Young,
        "middleaged" or "middle-aged" or "middle_aged" => VoiceAge.MiddleAged,
        "elderly" or "old" => VoiceAge.Elderly,
        _ => VoiceAge.Adult,
    };

    /// <summary>Maps the LLM's free-ish accent word onto our menu (tolerant of synonyms).</summary>
    public static VoiceAccent ToVoiceAccent(this string? accent)
    {
        var a = accent?.Trim().ToLowerInvariant() ?? "";
        return a switch
        {
            "american" or "us" or "general american" => VoiceAccent.American,
            "british" or "english" or "uk" or "rp" => VoiceAccent.British,
            "french" => VoiceAccent.French,
            "italian" => VoiceAccent.Italian,
            "spanish" or "castilian" or "latin" => VoiceAccent.Spanish,
            "hindi" or "indian" or "south asian" or "south-asian" => VoiceAccent.Hindi,
            "japanese" => VoiceAccent.Japanese,
            "portuguese" or "brazilian" => VoiceAccent.Portuguese,
            "chinese" or "mandarin" => VoiceAccent.Chinese,
            "german" or "deutsch" => VoiceAccent.German,
            "russian" => VoiceAccent.Russian,
            "arabic" or "arab" or "gulf" or "emirati" => VoiceAccent.Arabic,
            "korean" => VoiceAccent.Korean,
            "thai" => VoiceAccent.Thai,
            "vietnamese" => VoiceAccent.Vietnamese,
            "turkish" => VoiceAccent.Turkish,
            "scottish" or "scots" or "scot" => VoiceAccent.Scottish,
            "irish" => VoiceAccent.Irish,
            "australian" or "aussie" => VoiceAccent.Australian,
            _ => VoiceAccent.Unknown,
        };
    }

    public static string Label(this VoiceGender g) => g switch
    {
        VoiceGender.Male => "male",
        VoiceGender.Female => "female",
        _ => "neutral",
    };

    public static string Label(this VoiceAccent a) => a switch
    {
        VoiceAccent.Unknown => "any",
        _ => a.ToString().ToLowerInvariant(),
    };
}
