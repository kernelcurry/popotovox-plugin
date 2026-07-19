using System;
using System.Collections.Generic;
using System.Linq;
using PopotoVox.Infrastructure;

namespace PopotoVox.Tts;

/// <summary>
/// Metadata describing one selectable voice engine — its quality tier, hardware
/// needs, capabilities, and which manifest assets it requires.
/// </summary>
public sealed record TtsEngineInfo(
    TtsEngineChoice Id,
    string DisplayName,
    int Tier,
    bool RequiresGpu,
    bool SupportsEmotion,
    int SpeakerCount,
    IReadOnlyList<string> AssetIds,
    string Notes,
    string Tagline,
    string Summary,
    bool Recommended = false,
    bool SupportsStreaming = false)
{
    /// <summary>Plain-language hardware line for the setup wizard.</summary>
    public string Hardware => RequiresGpu ? "NVIDIA GPU required" : "Runs on any CPU";

    /// <summary>Quality label derived from the tier.</summary>
    public string Quality => Tier switch
    {
        >= 2 => "Best — expressive",
        1 => "Great — natural",
        _ => "Basic — lightweight",
    };
}

/// <summary>
/// The registry of voice engines (a quality ladder). Adding an engine means adding
/// one descriptor here plus a branch in <see cref="Create"/> — nothing else hardcodes
/// the engine list (UI, downloads, and selection all read from this catalog).
/// </summary>
public static class TtsEngineCatalog
{
    public static readonly IReadOnlyList<TtsEngineInfo> All = new[]
    {
        new TtsEngineInfo(
            TtsEngineChoice.Kokoro, "Kokoro", Tier: 1, RequiresGpu: false, SupportsEmotion: false,
            SpeakerCount: KokoroEngine.SpeakerCount,
            AssetIds: new[] { "kokoro-multi-lang-v1_0" },
            Notes: "Natural-sounding, runs on CPU. The default.",
            Tagline: "Natural voices with real-world accents, works on any PC",
            Summary: "Clear, natural-sounding speech with 53 voices spanning nine real-world accents " +
                     "(British, French, Japanese, Hindi and more) — so NPCs from different lands sound " +
                     "the part. Runs entirely on your processor — no graphics card needed. The best " +
                     "starting point for almost everyone.",
            Recommended: true),

        new TtsEngineInfo(
            TtsEngineChoice.Piper, "Piper", Tier: 0, RequiresGpu: false, SupportsEmotion: false,
            SpeakerCount: PiperEngine.SpeakerCount,
            AssetIds: new[] { "piper", "libritts-high.onnx", "libritts-high.onnx.json" },
            Notes: "Lightweight and fast, but robotic. CPU.",
            Tagline: "Fastest and lightest, more robotic",
            Summary: "The smallest, fastest option with 904 voices. Voices sound a bit more " +
                     "robotic than Kokoro, but it uses very little memory and starts instantly. " +
                     "Good for low-end machines or if you want maximum responsiveness."),

        new TtsEngineInfo(
            // Single-model Ultra: designs a per-NPC reference in the speaker's native tongue (real accent),
            // then clones it per line with optional per-line emotion. SpeakerCount 0 = designed per NPC.
            // Assets = the packaged portable Python runtime + model snapshot (the host script ships in
            // the plugin zip itself). A hand-installed voxcpm-dev.json still overrides both.
            TtsEngineChoice.VoxCPM2, "VoxCPM2", Tier: 3, RequiresGpu: true, SupportsEmotion: true,
            SupportsStreaming: true, // autoregressive → can stream PCM as it generates (first sound sooner)
            SpeakerCount: 0,
            AssetIds: new[] { "voxcpm2-runtime", "voxcpm2-model" },
            Notes: "Designs a unique accented voice for every NPC, then performs each line. One local AI model; NVIDIA GPU.",
            Tagline: "Designs a one-of-a-kind voice for every NPC",
            Summary: "The most lifelike tier. Designs a unique voice for each NPC from the casting description " +
                     "and their homeland's accent, then performs every line by cloning that voice — with optional " +
                     "per-line emotion. One local AI model; requires an NVIDIA graphics card. The first line for a " +
                     "newly-met NPC takes a moment while its voice is designed (then it's cached)."),
    };

    public static TtsEngineInfo Get(TtsEngineChoice id) =>
        All.FirstOrDefault(e => e.Id == id) ?? All.First(e => e.Id == TtsEngineChoice.Kokoro);

    public static ITtsEngine Create(TtsEngineChoice id, PluginPaths paths, string pluginDir, Configuration config) =>
        id switch
        {
            TtsEngineChoice.Piper => new PiperEngine(paths, config.GlobalVoiceParams()),
            TtsEngineChoice.Kokoro => new KokoroEngine(paths, pluginDir),
            TtsEngineChoice.VoxCPM2 => new VoxCpmEngine(paths, pluginDir, () => config),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown TTS engine."),
        };
}
