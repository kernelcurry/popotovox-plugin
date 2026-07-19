namespace PopotoVox.Tts;

/// <summary>
/// A soft real-world accent *lean* derived from an NPC's tribe (game data). This is ADVISORY only:
/// it feeds an overridable hint into the casting prompt and labels the NPC card — it never forces an
/// accent. The casting LLM still decides, weighing this lean against the character's dialogue, name,
/// and location (a Sea Wolf raised in Kugane should still sound Japanese). Only tribes with an
/// unambiguous cultural flavour are listed; the rest return <see cref="VoiceAccent.Unknown"/>.
/// </summary>
internal static class AccentLore
{
    public static VoiceAccent TribeLean(string? tribe) => tribe switch
    {
        "Raen" => VoiceAccent.Japanese,      // Au Ra, Far East
        "Xaela" => VoiceAccent.Chinese,      // Au Ra, Azim Steppe nomads
        "Sea Wolf" => VoiceAccent.Italian,   // Roegadyn, Limsa / seafaring
        "Dunesfolk" => VoiceAccent.Spanish,  // Lalafell, Ul'dah / desert
        _ => VoiceAccent.Unknown,
    };
}
