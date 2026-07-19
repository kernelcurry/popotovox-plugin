using Dalamud.Configuration;
using PopotoVox.Tts;

namespace PopotoVox;

public enum IndicatorCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>How widely to pre-build NPC voices in the background (cast + design before the player talks).
/// Off is the default; wider tiers trade more background GPU/LLM + disk for more first-lines being instant.</summary>
public enum PrecomputeRange
{
    Off,
    Near,   // a few nearest NPCs
    Mid,    // a larger radius / more NPCs
    Zone,   // every NPC currently loaded around the player
}

public enum TtsEngineChoice
{
    Kokoro = 0, // sherpa-onnx, natural, 53 voices across 9 real-world accents — High tier (casting-driven)
    Piper = 1,  // standalone .exe, 904 voices, robotic but lightweight — Low/Medium tier
    // 2 (Orpheus) and 3 (Studio) removed — those engines are deleted. Old configs migrate off them by
    // numeric value (see Plugin.MigrateConfiguration v7). VoxCPM2 stays pinned at 4 so stored configs
    // (which serialize the enum as an int) don't shift.
    VoxCPM2 = 4, // GPU, single model: native-tongue reference design -> clone per line (+ emotion) — Ultra
}

/// <summary>
/// "Graphics-menu" style quality presets that bundle the meaningful voice knobs from fastest/
/// lightest (Low) to most lifelike (Ultra). <see cref="Custom"/> means the current settings don't
/// match any preset (the user tuned things by hand).
/// </summary>
public enum QualityPreset
{
    Low,    // Piper, rules-only — instant, runs on any CPU
    Medium, // Piper + smart casting — lightweight, CPU
    High,   // Kokoro + smart casting — natural, CPU
    Ultra,  // VoxCPM2 (designed voice per NPC) + smart casting + emotion — most lifelike, GPU
    Custom,
}


[System.Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 7;

    // --- TTS engine --- (Kokoro runs in an isolated helper process, so it's the safe default)
    public TtsEngineChoice TtsEngine { get; set; } = TtsEngineChoice.Kokoro;

    // Stream audio out as it's generated (first sound starts seconds sooner) instead of waiting for the whole
    // line. Only used by engines whose catalog entry has SupportsStreaming (VoxCPM2/Ultra); on by default
    // there. If the GPU can't generate faster than real-time it stutters — the pipeline warns and the user
    // can turn this off (the UI toggle is disabled on engines that don't stream).
    public bool StreamAudio { get; set; } = true;

    // --- Casting ---
    public bool LlmEnabled { get; set; } = true;
    public float LlmTemperature { get; set; } = 0.3f;
    public int CastWaitTimeoutSeconds { get; set; } = 90;

    // Direct each line's delivery via the LLM (emotion-capable engines only, i.e. VoxCPM2/Ultra).
    // This is an extra LLM call before every line; turning it off trades a little expressiveness
    // for noticeably faster lines.
    public bool EmotionAnnotation { get; set; } = true;

    // --- LLM runtime ---
    public int LlmPort { get; set; } = 8757;
    public int LlmContextSize { get; set; } = 4096;
    public int LlmGpuLayers { get; set; } = 0;   // CPU by default (PRD §11)
    public int LlmThreads { get; set; } = 0;      // 0 = let llama-server decide

    // --- Voice (global defaults, PRD §9) ---
    public float GlobalLengthScale { get; set; } = 1.0f;
    public float GlobalNoiseScale { get; set; } = 0.667f;
    public float GlobalNoiseW { get; set; } = 0.8f;
    public float Volume { get; set; } = 1.0f;

    // --- Dialogue sources --- (read the in-world dialogue boxes by default; the chat-log source
    // is OFF by default because the game echoes many dialogue-box lines to chat, which made the
    // same line get voiced twice — once from the box, once from the chat echo.)
    public bool CaptureChatGui { get; set; }                       // chat log (off by default)
    public bool CaptureAddonTalk { get; set; } = true;             // main dialogue window
    public bool CaptureAddonBattleTalk { get; set; } = true;       // mid-battle dialogue banner
    public bool CaptureMiniTalk { get; set; }                      // overhead NPC speech bubbles (ambient, off by default)

    // --- Performance: cached line audio (repeats play instantly + skip the GPU) ---
    public bool LineCacheEnabled { get; set; } = true;
    public int LineCacheRetentionHours { get; set; } = 5;

    // --- Performance: Ultra (VoxCPM2) per-voice design WAVs (cache/voices) — age + size LRU so the folder
    // can't grow without bound (a design WAV is the reusable reference for every line of that voice). ---
    public int VoicesCacheRetentionHours { get; set; } = 168; // 1 week — designs are expensive to rebuild
    public int VoicesCacheMaxMB { get; set; } = 512;

    // --- Audio mixer: several voice lines can play AT ONCE (overlapping ambient bubbles, each
    // distance-attenuated + spatially tracked). MaxConcurrentVoices caps simultaneous PLAYBACK
    // (mixer inputs). MaxConcurrentRenders caps simultaneous synthesis: the GPU engine (VoxCPM2)
    // uses ONE host process, so >1 just thrashes — keep it 1; the win is playback overlapping even
    // though renders serialize. CPU engines (Kokoro/Piper) can raise it. 0 = auto (machine-sized). ---
    public int MaxConcurrentVoices { get; set; }              // 0 = auto: clamp(ProcessorCount, 4, 16)
    public int MaxConcurrentRenders { get; set; } = 1;

    // --- Performance: pre-build nearby NPC voices in the background (Off by default) ---
    public PrecomputeRange NpcPrecompute { get; set; } = PrecomputeRange.Off;

    // --- Ambient overhead bubbles: distance-based volume + pre-render (M12) ---
    public bool AmbientDistanceVolume { get; set; } = true;   // fade bubble volume with distance
    public bool AmbientSpatialTracking { get; set; } = true;  // M16: update volume live as you move past the speaker
    public int AmbientHearingYalms { get; set; } = 12;        // M16: full within ~3y, realistic inverse-distance falloff, silent here
    public bool PrerenderAmbientLines { get; set; }           // pre-render nearby NPCs' known ambient lines (off)
    public int PrerenderAmbientYalms { get; set; } = 15;      // only pre-render ambient lines within this range

    // --- UX ---
    public bool ShowCastingIndicator { get; set; } = true;
    public IndicatorCorner IndicatorPosition { get; set; } = IndicatorCorner.BottomRight;
    public bool StatusMessages { get; set; } = true;

    /// <summary>False until the user has been through (or dismissed) the first-run setup wizard.</summary>
    public bool SetupCompleted { get; set; }

    public VoiceParams GlobalVoiceParams() => new()
    {
        LengthScale = GlobalLengthScale,
        NoiseScale = GlobalNoiseScale,
        NoiseW = GlobalNoiseW,
    };

    /// <summary>Apply a quality preset by setting the bundle of knobs it represents. The ladder:
    /// Low = Piper (no casting) · Medium = Piper + casting · High = Kokoro + casting ·
    /// Ultra = VoxCPM2 + casting + emotion. Casting is required (and forced on) for High and Ultra.</summary>
    public void ApplyQualityPreset(QualityPreset preset)
    {
        switch (preset)
        {
            case QualityPreset.Low:
                TtsEngine = TtsEngineChoice.Piper; LlmEnabled = false; EmotionAnnotation = false; break;
            case QualityPreset.Medium:
                TtsEngine = TtsEngineChoice.Piper; LlmEnabled = true; EmotionAnnotation = false; break;
            case QualityPreset.High:
                TtsEngine = TtsEngineChoice.Kokoro; LlmEnabled = true; EmotionAnnotation = false; break;
            case QualityPreset.Ultra:
                TtsEngine = TtsEngineChoice.VoxCPM2; LlmEnabled = true; EmotionAnnotation = true; break;
            case QualityPreset.Custom: break; // nothing to apply — caller-driven custom tuning
        }
    }

    /// <summary>Which preset the current settings match, or <see cref="QualityPreset.Custom"/>. Engine alone
    /// no longer distinguishes tiers (Piper = Low <b>and</b> Medium, Kokoro = High), so key on the
    /// (engine, casting) pair. Emotion is toggleable on Ultra, so it isn't part of the match.</summary>
    public QualityPreset DetectQualityPreset() => (TtsEngine, LlmEnabled) switch
    {
        (TtsEngineChoice.Piper, false) => QualityPreset.Low,
        (TtsEngineChoice.Piper, true) => QualityPreset.Medium,
        (TtsEngineChoice.Kokoro, true) => QualityPreset.High,
        (TtsEngineChoice.VoxCPM2, true) => QualityPreset.Ultra,
        _ => QualityPreset.Custom,
    };

    /// <summary>Engines that cannot run without the casting AI (the UI locks the toggle on + forces it).
    /// Kokoro (High) and VoxCPM2 (Ultra) both need the LLM to pick/describe each NPC's voice.</summary>
    public bool CastingRequired => TtsEngine is TtsEngineChoice.Kokoro or TtsEngineChoice.VoxCPM2;

    /// <summary>A detached deep copy for the settings "draft" (edit-then-Apply). All fields are value types
    /// (enums/bool/int/float), so a member-wise copy is a perfect clone — nothing is shared with the live
    /// config until <see cref="CopyFrom"/> commits it. <see cref="MemberwiseClone"/> keeps this in lock-step
    /// with the field list automatically as knobs are added.</summary>
    public Configuration Clone() => (Configuration)MemberwiseClone();

    /// <summary>Commit a draft's values back into this (the live, Dalamud-registered) instance in place, so the
    /// closures subsystems captured (<c>() =&gt; Configuration</c>) and <see cref="Save"/> all target it.</summary>
    public void CopyFrom(Configuration d)
    {
        Version = d.Version;
        TtsEngine = d.TtsEngine;
        StreamAudio = d.StreamAudio;
        LlmEnabled = d.LlmEnabled;
        LlmTemperature = d.LlmTemperature;
        CastWaitTimeoutSeconds = d.CastWaitTimeoutSeconds;
        EmotionAnnotation = d.EmotionAnnotation;
        LlmPort = d.LlmPort;
        LlmContextSize = d.LlmContextSize;
        LlmGpuLayers = d.LlmGpuLayers;
        LlmThreads = d.LlmThreads;
        GlobalLengthScale = d.GlobalLengthScale;
        GlobalNoiseScale = d.GlobalNoiseScale;
        GlobalNoiseW = d.GlobalNoiseW;
        Volume = d.Volume;
        CaptureChatGui = d.CaptureChatGui;
        CaptureAddonTalk = d.CaptureAddonTalk;
        CaptureAddonBattleTalk = d.CaptureAddonBattleTalk;
        CaptureMiniTalk = d.CaptureMiniTalk;
        LineCacheEnabled = d.LineCacheEnabled;
        LineCacheRetentionHours = d.LineCacheRetentionHours;
        VoicesCacheRetentionHours = d.VoicesCacheRetentionHours;
        VoicesCacheMaxMB = d.VoicesCacheMaxMB;
        MaxConcurrentVoices = d.MaxConcurrentVoices;
        MaxConcurrentRenders = d.MaxConcurrentRenders;
        NpcPrecompute = d.NpcPrecompute;
        AmbientDistanceVolume = d.AmbientDistanceVolume;
        AmbientSpatialTracking = d.AmbientSpatialTracking;
        AmbientHearingYalms = d.AmbientHearingYalms;
        PrerenderAmbientLines = d.PrerenderAmbientLines;
        PrerenderAmbientYalms = d.PrerenderAmbientYalms;
        ShowCastingIndicator = d.ShowCastingIndicator;
        IndicatorPosition = d.IndicatorPosition;
        StatusMessages = d.StatusMessages;
        SetupCompleted = d.SetupCompleted;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
