using System.Collections.Generic;
using System.Linq;

namespace PopotoVox.Tts;

/// <summary>
/// The labeled voice roster for each engine — the source of truth that lets casting
/// pick a gender- and character-appropriate voice instead of a blind integer.
///
/// Piper/libritts's 904 voices are gender-labeled from a generated table
/// (<see cref="PiperVoiceGenders"/>), and Kokoro exposes its voices labeled by the
/// af_/am_/bf_/bm_ name convention (the model's 2nd char is the gender, the 1st the accent).
/// VoxCPM2 (Ultra) has no fixed pool — it designs a voice per NPC.
/// </summary>
public static class VoicePalette
{
    public static IReadOnlyList<VoiceInfo> For(TtsEngineChoice engine) => engine switch
    {
        TtsEngineChoice.Kokoro => Kokoro,
        TtsEngineChoice.Piper => Piper,
        _ => Neutral(1), // VoxCPM2 designs a voice per NPC — no fixed pool.
    };

    /// <summary>
    /// kokoro-multi-lang-v1_0's 53 voices (the model's sid→name map order). Each name encodes
    /// region+gender: 1st char = accent (a=American, b=British, e=Spanish, f=French, h=Hindi,
    /// i=Italian, j=Japanese, p=Portuguese, z=Chinese), 2nd char = gender (f/m). All speak
    /// English; the non-American ones carry their region's accent — that's the point (region-
    /// aware casting). No per-voice age/timbre, so within a gender+accent the matcher spreads
    /// deterministically.
    /// </summary>
    public static readonly IReadOnlyList<VoiceInfo> Kokoro = BuildKokoro();

    private static IReadOnlyList<VoiceInfo> BuildKokoro()
    {
        // The 53 voices in the model's sid→name order. Kept LOCAL (not a static field) so this
        // can never depend on static field initialization order — a static field read here ran
        // before its own initializer and threw at plugin load.
        string[] names =
        {
            "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica", "af_kore",      // 0–5
            "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",                     // 6–10
            "am_adam", "am_echo", "am_eric", "am_fenrir", "am_liam", "am_michael",        // 11–16
            "am_onyx", "am_puck", "am_santa",                                             // 17–19
            "bf_alice", "bf_emma", "bf_isabella", "bf_lily",                              // 20–23
            "bm_daniel", "bm_fable", "bm_george", "bm_lewis",                             // 24–27
            "ef_dora", "em_alex",                                                         // 28–29 Spanish
            "ff_siwis",                                                                   // 30 French (female only)
            "hf_alpha", "hf_beta", "hm_omega", "hm_psi",                                  // 31–34 Hindi
            "if_sara", "im_nicola",                                                       // 35–36 Italian
            "jf_alpha", "jf_gongitsune", "jf_nezumi", "jf_tebukuro", "jm_kumo",           // 37–41 Japanese
            "pf_dora", "pm_alex", "pm_santa",                                             // 42–44 Portuguese
            "zf_xiaobei", "zf_xiaoni", "zf_xiaoxiao", "zf_xiaoyi",                        // 45–48 Chinese
            "zm_yunjian", "zm_yunxi", "zm_yunxia", "zm_yunyang",                          // 49–52 Chinese
        };
        return names
            .Select((name, id) => new VoiceInfo(
                id, name,
                name[1] == 'm' ? VoiceGender.Male : VoiceGender.Female, // 2nd char = gender
                VoiceAge.Adult, "",
                AccentFromPrefix(name[0])))                             // 1st char = accent/region
            .ToList();
    }

    private static VoiceAccent AccentFromPrefix(char c) => c switch
    {
        'a' => VoiceAccent.American,
        'b' => VoiceAccent.British,
        'e' => VoiceAccent.Spanish,
        'f' => VoiceAccent.French,
        'h' => VoiceAccent.Hindi,
        'i' => VoiceAccent.Italian,
        'j' => VoiceAccent.Japanese,
        'p' => VoiceAccent.Portuguese,
        'z' => VoiceAccent.Chinese,
        _ => VoiceAccent.American,
    };

    /// <summary>
    /// libritts-high's 904 speakers, gender-labeled from <see cref="PiperVoiceGenders"/>.
    /// No age/timbre metadata exists for these readers, so within a gender the matcher
    /// falls back to a deterministic spread — which is exactly right for the rules-only
    /// Low preset, and still gender-correct when the LLM is on. Falls back to a Neutral
    /// palette if the table ever desyncs from the model's speaker count.
    /// </summary>
    public static readonly IReadOnlyList<VoiceInfo> Piper = BuildPiper();

    private static IReadOnlyList<VoiceInfo> BuildPiper()
    {
        var g = PiperVoiceGenders.ById;
        if (g.Length != PiperEngine.SpeakerCount) return Neutral(PiperEngine.SpeakerCount);
        return Enumerable.Range(0, g.Length)
            .Select(i => new VoiceInfo(
                i, $"voice {i}",
                g[i] switch { 'M' => VoiceGender.Male, 'F' => VoiceGender.Female, _ => VoiceGender.Neutral },
                VoiceAge.Adult, ""))
            .ToList();
    }

    private static IReadOnlyList<VoiceInfo> Neutral(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new VoiceInfo(i, $"voice {i}", VoiceGender.Neutral, VoiceAge.Adult, ""))
            .ToList();
}
