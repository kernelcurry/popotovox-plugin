using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using PopotoVox.Infrastructure;

namespace PopotoVox.Tts;

/// <summary>
/// The "VoxCPM2" voice engine — the Ultra tier. One 2B model (in an isolated Python helper) both
/// <b>designs</b> a unique reference voice per NPC (once, cached — a native-tongue line gives it a real
/// accent) and <b>clones</b> that reference to speak each line, optionally performing a per-line emotion
/// <c>(style)</c> direction. One model handles both phases. A Python/GPU fault kills only the helper,
/// never the game.
///
/// Dev: paths come from <c>voxcpm-dev.json</c> in the plugin config dir (hand-installed venv). Packaging
/// (downloadable runtime+model) is a later milestone.
/// </summary>
public sealed class VoxCpmEngine : ITtsEngine
{
    private const int VoxSampleRate = 48000; // VoxCPM2 PCM rate; the host also reports it via the SR handshake.

    private readonly VoxCpmRuntime? runtime;
    private readonly string voicesCache;
    private readonly string tempDir;
    private readonly object gate = new();
    private readonly Func<Configuration>? config;

    private VoxCpmHostProcess? host;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> designing = new(); // dedupe same-voice designs

    public VoxCpmEngine(PluginPaths paths, string pluginDir, Func<Configuration>? config = null)
    {
        voicesCache = paths.VoicesCache;
        tempDir = paths.PiperTemp;
        this.config = config;
        runtime = VoxCpmRuntime.Resolve(paths, pluginDir);
        PruneVoicesCache(); // drop stale/oversized reference WAVs from previous sessions
    }

    public bool IsReady => runtime is { } r
        && File.Exists(r.Python) && File.Exists(r.Script);

    private static (bool present, DateTime at) runtimeProbe; // cached — the UI asks every frame

    /// <summary>Whether the VoxCPM2 runtime is present, without building an engine — mirrors
    /// <see cref="IsReady"/> for the setup/Settings tiles (dev-config override OR the packaged
    /// downloaded layout). Cached for a couple of seconds so per-frame UI polling doesn't hit
    /// the filesystem.</summary>
    public static bool RuntimePresent(PluginPaths paths, string pluginDir)
    {
        if ((DateTime.UtcNow - runtimeProbe.at).TotalSeconds < 2) return runtimeProbe.present;
        var ok = VoxCpmRuntime.Resolve(paths, pluginDir) != null;
        runtimeProbe = (ok, DateTime.UtcNow);
        return ok;
    }

    public async Task<RenderedAudio> RenderAsync(string text, VoiceSpec spec, RenderContext? ctx = null, CancellationToken ct = default)
    {
        EnsureReady();
        var refWav = await EnsureReferencedAsync(spec, ctx, ct).ConfigureAwait(false);
        return await Host().SynthesizeAsync(refWav, SpeechText.Normalize(text), ctx?.EmotionPreset, ct).ConfigureAwait(false);
    }

    /// <summary>Clone the reference voice and stream the PCM to <paramref name="sink"/> as it's generated, so
    /// the line starts playing before it finishes. Designs the reference first (cached) exactly like the
    /// whole-line path, then streams the clone.</summary>
    public async Task RenderStreamingAsync(string text, VoiceSpec spec, IAudioSink sink, RenderContext? ctx = null, CancellationToken ct = default)
    {
        EnsureReady();
        var refWav = await EnsureReferencedAsync(spec, ctx, ct).ConfigureAwait(false);
        await Host().StreamAsync(refWav, SpeechText.Normalize(text), ctx?.EmotionPreset, sink, ct).ConfigureAwait(false);
    }

    /// <summary>Load the model now (design a default voice + a tiny clone) so the first NPC line isn't lost
    /// to the cold start. Best-effort and silent.</summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        if (!IsReady) return;
        try
        {
            var spec = new VoiceSpec { Gender = VoiceGender.Male, Traits = new VoiceTraits(VoiceAge.Adult, "", VoiceAccent.American) };
            var refWav = await EnsureReferencedAsync(spec, null, ct).ConfigureAwait(false);
            await Host().SynthesizeAsync(refWav, "Hm.", style: null, ct).ConfigureAwait(false);
        }
        catch { /* warm-up is best-effort; a real render surfaces any genuine fault */ }
    }

    /// <summary>Design (and cache) this NPC's reference voice ahead of time so their first spoken line skips
    /// the design cold start. Best-effort.</summary>
    public async Task PredesignAsync(VoiceSpec spec, CancellationToken ct = default)
    {
        if (!IsReady) return;
        try { await EnsureReferencedAsync(spec, null, ct).ConfigureAwait(false); }
        catch { /* best-effort precompute */ }
    }

    /// <summary>Design the reference voice for this NPC if we haven't already; returns the cached WAV path.</summary>
    private async Task<string> EnsureReferencedAsync(VoiceSpec spec, RenderContext? ctx, CancellationToken ct)
    {
        var key = VoxCpmReferenceBuilder.CacheKey(spec);
        var refWav = Path.Combine(voicesCache, key + ".wav");
        if (File.Exists(refWav))
        {
            // LRU bump: this reference is reused for EVERY line of this voice, so keep it hot.
            try { File.SetLastWriteTimeUtc(refWav, DateTime.UtcNow); } catch { /* best-effort */ }
            return refWav;
        }

        // Dedupe concurrent designs of the SAME voice (two nearby NPCs sharing a voice). Lazy guarantees a
        // single invocation even though ConcurrentDictionary may call the factory more than once.
        var lazy = designing.GetOrAdd(key, _ => new Lazy<Task<string>>(() => DesignCore(spec, refWav, ctx, ct)));
        try { return await lazy.Value.ConfigureAwait(false); }
        finally { designing.TryRemove(key, out _); }
    }

    private async Task<string> DesignCore(VoiceSpec spec, string refWav, RenderContext? ctx, CancellationToken ct)
    {
        if (File.Exists(refWav)) return refWav;
        ctx?.OnDesigning?.Invoke(); // first encounter with this voice — light the one-time Design stage
        Directory.CreateDirectory(voicesCache);
        var desc = VoxCpmReferenceBuilder.Description(spec);
        var refText = VoxCpmReferenceBuilder.ReferenceText(spec);
        var seed = VoxCpmReferenceBuilder.Seed(spec);
        var produced = await Host().DesignAsync(desc, refText, seed, refWav, ct).ConfigureAwait(false);
        if (config is { } c) TrimVoicesBySize(voicesCache, c().VoicesCacheMaxMB);
        return File.Exists(refWav) ? refWav : produced;
    }

    private VoxCpmHostProcess Host()
    {
        var r = runtime!;
        lock (gate)
        {
            host ??= new VoxCpmHostProcess(r.Python, r.Script, r.Model, tempDir);
            return host;
        }
    }

    private void EnsureReady()
    {
        if (!IsReady) throw new InvalidOperationException("VoxCPM2 engine is not installed yet (missing voxcpm-dev.json or runtime).");
    }

    // ---- reference-cache maintenance (the per-NPC designed-voice cache; used by the Storage UI) --------

    private void PruneVoicesCache()
    {
        if (config is { } c) PruneVoicesCache(voicesCache, c().VoicesCacheRetentionHours, c().VoicesCacheMaxMB);
    }

    /// <summary>Total bytes of cached reference WAVs (for the Storage UI).</summary>
    public static long VoicesCacheBytes(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.wav").Sum(f => new FileInfo(f).Length) : 0L; }
        catch { return 0L; }
    }

    /// <summary>Delete every cached reference WAV (for the Storage UI "Clear now").</summary>
    public static void ClearVoicesCache(string dir)
    {
        try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir, "*.wav")) try { File.Delete(f); } catch { /* best-effort */ } }
        catch { /* best-effort */ }
    }

    /// <summary>Drop reference WAVs older than the retention, then enforce the size cap (LRU by mtime).</summary>
    public static void PruneVoicesCache(string dir, int retentionHours, int maxMB)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(1, retentionHours));
            foreach (var f in Directory.EnumerateFiles(dir, "*.wav"))
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                    try { File.Delete(f); } catch { /* best-effort */ }
            TrimVoicesBySize(dir, maxMB);
        }
        catch { /* best-effort */ }
    }

    private static void TrimVoicesBySize(string dir, int maxMB)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var maxBytes = (long)Math.Max(1, maxMB) * 1024 * 1024;
            var files = Directory.EnumerateFiles(dir, "*.wav").Select(f => new FileInfo(f)).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= maxBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }

    public void Dispose()
    {
        lock (gate)
        {
            host?.Dispose(); host = null;
        }
    }

    /// <summary>Resolved runtime locations for the VoxCPM2 Python helper. The hand-installed
    /// dev-config (voxcpm-dev.json) always wins; otherwise the packaged layout — the downloaded
    /// portable runtime + model (signed-manifest assets) with the host script shipped in the
    /// plugin's own output.</summary>
    private sealed record VoxCpmRuntime(string Python, string Script, string? Model)
    {
        public static VoxCpmRuntime? Resolve(PluginPaths paths, string pluginDir)
        {
            var dev = TryLoad(paths.VoxCpmDevConfigPath);
            if (dev != null) return dev;

            var python = Path.Combine(paths.VoxCpmRuntimeDir, "python.exe");
            var script = Path.Combine(pluginDir, "voxcpm-host", "voxcpm2_host.py");
            var model = paths.VoxCpmModelDir;
            // config.json is the model-snapshot sentinel: present ⇒ the zip extracted fully.
            if (File.Exists(python) && File.Exists(script) && File.Exists(Path.Combine(model, "config.json")))
                return new VoxCpmRuntime(python, script, model);
            return null;
        }

        public static VoxCpmRuntime? TryLoad(string devConfigPath)
        {
            try
            {
                if (!File.Exists(devConfigPath)) return null;
                var dev = JsonSerializer.Deserialize<DevConfig>(File.ReadAllText(devConfigPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dev?.VoxPython is null || dev.HostDir is null) return null;
                return new VoxCpmRuntime(dev.VoxPython, Path.Combine(dev.HostDir, "voxcpm2_host.py"), dev.VoxModel);
            }
            catch
            {
                return null;
            }
        }

        private sealed class DevConfig
        {
            [JsonPropertyName("voxPython")] public string? VoxPython { get; init; }
            [JsonPropertyName("hostDir")] public string? HostDir { get; init; }
            [JsonPropertyName("voxModel")] public string? VoxModel { get; init; }
        }
    }
}
