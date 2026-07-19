namespace PopotoVox.Tts;

/// <summary>
/// One selectable voice in an engine's palette, labeled so the casting layer can
/// match a character to it (gender, age, and a few timbre keywords). This is the
/// metadata the old <see cref="SpeakerCatalog"/> lacked — without it the LLM/rules
/// could only pick a meaningless integer, so a male NPC could land on a female voice.
/// </summary>
/// <param name="Id">The engine's speaker id (what gets rendered).</param>
/// <param name="Name">Human-facing voice name (e.g. "leo", "af_bella").</param>
/// <param name="Gender">Voice gender, used to gate selection to the NPC's gender.</param>
/// <param name="AgeRank">Coarse age the voice reads as, for trait scoring.</param>
/// <param name="Descriptor">Short timbre keywords ("warm, steady"), for trait scoring + the bio card.</param>
/// <param name="Accent">Real-world accent the voice reads as in English, for region-aware casting.</param>
public sealed record VoiceInfo(
    int Id,
    string Name,
    VoiceGender Gender,
    VoiceAge AgeRank,
    string Descriptor,
    VoiceAccent Accent = VoiceAccent.American);
