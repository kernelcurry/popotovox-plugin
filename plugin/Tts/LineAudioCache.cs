using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using NAudio.Wave;

namespace PopotoVox.Tts;

/// <summary>
/// Caches the FINAL rendered audio of a spoken line, so a repeated line plays instantly and skips the
/// GPU entirely (FFXIV NPCs repeat lines a lot; this also gives weaker PCs a break). Keyed by the NPC's
/// voice identity on the active engine + the line text; persistent under <c>cache/lines</c>, pruned by
/// age (the user's retention) on startup + a size cap (LRU by file mtime). Everything is best-effort —
/// any failure is just a cache miss (re-synthesize).
/// </summary>
public sealed class LineAudioCache
{
    private const long MaxBytes = 300L * 1024 * 1024; // safety cap; oldest evicted first

    private readonly string dir;
    private readonly Func<Configuration> config;
    private readonly IPluginLog log;

    public LineAudioCache(string dir, Func<Configuration> config, IPluginLog log)
    {
        this.dir = dir;
        this.config = config;
        this.log = log;
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
        Prune(); // drop previous-session lines older than the retention
    }

    public bool Enabled => config().LineCacheEnabled;

    /// <summary>
    /// Stable key for (this NPC's voice on the active engine) + (this line). Includes npcId so Ultra
    /// specs — which all share SpeakerId 0 — never collide onto one clip; includes engine + speakerId so
    /// switching engines / pooled voices key distinctly.
    /// </summary>
    public static string Key(VoiceSpec spec, string text)
    {
        var id = spec.NpcId?.ToString(CultureInfo.InvariantCulture)
                 ?? (string.IsNullOrEmpty(spec.InputHash) ? spec.SpeakerName : spec.InputHash);
        var material = $"{spec.Engine}|{id}|{spec.SpeakerId}|{spec.Params.LengthScale.ToString("0.###", CultureInfo.InvariantCulture)}|{NormalizeText(text)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)), 0, 16).ToLowerInvariant();
    }

    /// <summary>Conservative text normalization so a pre-rendered (static) line keys the same as the live
    /// (captured) line — they differ only by whitespace/case (M10 finding). Keeps punctuation (low collision).</summary>
    private static string NormalizeText(string? t) =>
        string.IsNullOrEmpty(t) ? "" : System.Text.RegularExpressions.Regex.Replace(t.Trim().ToLowerInvariant(), "\\s+", " ");

    /// <summary>Cheap existence check (no file read) so pre-render can skip already-cached lines.</summary>
    public bool Has(string key) => Enabled && File.Exists(Path.Combine(dir, key + ".wav"));

    public bool TryGet(string key, out RenderedAudio audio)
    {
        audio = null!;
        if (!Enabled) return false;
        var path = Path.Combine(dir, key + ".wav");
        try
        {
            if (!File.Exists(path)) return false;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                audio = WavReader.ReadOne(fs);
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* LRU bump is best-effort */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Put(string key, RenderedAudio audio)
    {
        if (!Enabled || audio?.Pcm16 is not { Length: > 0 }) return;
        var path = Path.Combine(dir, key + ".wav");
        try
        {
            using (var w = new WaveFileWriter(path, new WaveFormat(audio.SampleRate, 16, audio.Channels)))
                w.Write(audio.Pcm16, 0, audio.Pcm16.Length);
            TrimBySize();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[LineCache] write failed.");
        }
    }

    public void Clear()
    {
        try { foreach (var f in Directory.EnumerateFiles(dir, "*.wav")) File.Delete(f); }
        catch { /* best-effort */ }
    }

    public long Bytes()
    {
        try { return Directory.EnumerateFiles(dir, "*.wav").Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>Delete cached lines older than the configured retention, then enforce the size cap.
    /// Called on startup and whenever the retention setting changes.</summary>
    public void Prune()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(Math.Max(1, config().LineCacheRetentionHours));
            foreach (var f in Directory.EnumerateFiles(dir, "*.wav"))
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                    try { File.Delete(f); } catch { /* best-effort */ }
            TrimBySize();
        }
        catch { /* best-effort */ }
    }

    private void TrimBySize()
    {
        try
        {
            var files = Directory.EnumerateFiles(dir, "*.wav").Select(f => new FileInfo(f)).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= MaxBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= MaxBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { /* best-effort */ }
            }
        }
        catch { /* best-effort */ }
    }
}
