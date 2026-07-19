using System.IO;

namespace PopotoVox.Infrastructure;

/// <summary>
/// Single source of truth for where PopotoVox keeps its on-disk state. Everything
/// lives under the Dalamud-provided per-plugin config directory so uninstalling the
/// plugin takes its data with it. Directories are created eagerly on construction.
/// </summary>
public sealed class PluginPaths
{
    public string Root { get; }
    public string Assets { get; }
    public string Staging { get; }
    public string Models { get; }
    public string PiperDir { get; }
    public string KokoroDir { get; }
    public string LlamaDir { get; }
    public string LlamaCudaDir { get; }
    public string Cache { get; }
    public string PiperTemp { get; }

    /// <summary>Persistent per-NPC designed reference WAVs (Ultra engine). NOT wiped on startup.</summary>
    public string VoicesCache { get; }

    /// <summary>Cached final rendered line audio (TTL'd, size-capped). NOT wiped on startup (pruned by age).</summary>
    public string LinesCache { get; }

    /// <summary>Dev-only config pointing at the hand-installed VoxCPM2 Python runtime (Ultra engine).</summary>
    public string VoxCpmDevConfigPath { get; }

    public string AuditLogPath { get; }
    public string VoiceSpecCachePath { get; }
    public string CrossLinkPath { get; }
    public string NpcRecordCachePath { get; }
    public string OverridesPath { get; }
    public string PresetsDir { get; }

    public PluginPaths(string configDirectory)
    {
        Root = configDirectory;
        Assets = Path.Combine(Root, "assets");
        Staging = Path.Combine(Assets, ".staging");
        Models = Path.Combine(Assets, "models");
        PiperDir = Path.Combine(Assets, "piper");
        KokoroDir = Path.Combine(Assets, "kokoro");
        LlamaDir = Path.Combine(Assets, "llama");
        LlamaCudaDir = Path.Combine(Assets, "llama-cuda");
        Cache = Path.Combine(Root, "cache");
        PiperTemp = Path.Combine(Cache, "tts");
        VoicesCache = Path.Combine(Cache, "voices");
        LinesCache = Path.Combine(Cache, "lines");
        PresetsDir = Path.Combine(Root, "presets");

        VoxCpmDevConfigPath = Path.Combine(Root, "voxcpm-dev.json");
        AuditLogPath = Path.Combine(Root, "downloads.log");
        VoiceSpecCachePath = Path.Combine(Cache, "voicespecs.json");
        CrossLinkPath = Path.Combine(Cache, "identity-crosslink.json");
        NpcRecordCachePath = Path.Combine(Cache, "npc-records.json");
        OverridesPath = Path.Combine(Root, "overrides.json");

        foreach (var dir in new[] { Root, Assets, Staging, Models, PiperDir, KokoroDir, LlamaDir, LlamaCudaDir, Cache, PiperTemp, VoicesCache, LinesCache, PresetsDir })
            Directory.CreateDirectory(dir);

        // Clear any temp WAVs a previous (possibly crashed) session left behind so the
        // per-utterance render dir can't accumulate files.
        try
        {
            foreach (var leftover in Directory.EnumerateFiles(PiperTemp, "*.wav"))
                File.Delete(leftover);
        }
        catch { /* best-effort cleanup */ }
    }
}
