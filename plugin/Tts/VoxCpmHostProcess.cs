using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PopotoVox.Tts;

/// <summary>
/// Owns the isolated VoxCPM2 Python helper — ONE process that designs a per-NPC reference voice
/// (<see cref="DesignAsync"/>) and clones it to speak each line, either whole (<see cref="SynthesizeAsync"/>)
/// or streamed chunk-by-chunk (<see cref="StreamAsync"/>) so a line starts playing before it finishes. At
/// startup the host prints "SR &lt;hz&gt;" (48 kHz). A Python/GPU fault kills only this helper, never the game.
///
/// ALL stdout is read from the raw <see cref="Stream"/> (never a StreamReader), so the text response lines
/// (design/clone paths, the SR handshake) and the binary PCM frames of a stream can share one stdout without
/// a StreamReader silently buffering bytes past a line and corrupting the following binary read.
/// </summary>
internal sealed class VoxCpmHostProcess : IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(180);    // cold model load
    private static readonly TimeSpan DesignTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(180);

    private readonly string pythonExe;
    private readonly string scriptPath;
    private readonly string? model;
    private readonly string tempDir;

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private static int counter;

    /// <summary>PCM sample rate the host writes at (read from the startup "SR" line; VoxCPM2 = 48 kHz).</summary>
    public int SampleRate { get; private set; } = 48000;

    public VoxCpmHostProcess(string pythonExe, string scriptPath, string? model, string tempDir)
    {
        this.pythonExe = pythonExe;
        this.scriptPath = scriptPath;
        this.model = model;
        this.tempDir = tempDir;
    }

    /// <summary>Diffusion decode steps for the one-time reference DESIGN (recipe B, user-auditioned
    /// 2026-07-19: 30 steps beat the 10-step baseline on both hiss and accent, with the clone identity
    /// intact). Clones/streams stay at the host's fast default (10) — this only slows the cached,
    /// once-per-voice design (~10 s → ~30 s), never a live line.</summary>
    private const int DesignTimesteps = 30;

    /// <summary>Design (voice-design) a reference voice from a description + native-tongue text; returns the
    /// written reference WAV path. One-shot; the caller caches the result per NPC.</summary>
    public async Task<string> DesignAsync(string desc, string refText, int seed, string outWav, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var proc = process!;
            await WriteRequestAsync(proc, new Request
            { Cmd = "design", Desc = desc, RefText = refText, Seed = seed, OutputFile = outWav, Timesteps = DesignTimesteps }, ct).ConfigureAwait(false);

            var signal = await ReadSignalAsync(proc, DesignTimeout).ConfigureAwait(false);
            if (signal == null) { KillProcess(); throw new InvalidOperationException("VoxCPM2 host closed stdout."); }
            if (signal.Length == 0) throw new EngineRenderException("VoxCPM2 host reported a design error.");
            return signal;
        }
        finally { gate.Release(); }
    }

    /// <summary>Clone the reference voice speaking <paramref name="text"/>, with an optional per-line
    /// <paramref name="style"/> emotion prefix. Non-streaming: writes a WAV and reads it back.</summary>
    public async Task<RenderedAudio> SynthesizeAsync(string refWav, string text, string? style, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var outPath = Path.Combine(tempDir, $"v{Interlocked.Increment(ref counter)}.wav");
        try
        {
            EnsureStarted();
            var proc = process!;
            await WriteRequestAsync(proc, new Request
            { Cmd = "clone", RefWav = refWav, Text = text, Style = style, OutputFile = outPath }, ct).ConfigureAwait(false);

            // Do NOT cancel mid-render (respawn is far costlier than letting a warm
            // render finish); foreground preemption happens at the gate. Kill only on hang / dead host.
            var signal = await ReadSignalAsync(proc, RenderTimeout).ConfigureAwait(false);
            if (signal == null) { KillProcess(); throw new InvalidOperationException("VoxCPM2 host closed stdout."); }
            if (signal.Length == 0) throw new EngineRenderException("VoxCPM2 host reported a render error.");

            try { return ReadRendered(outPath); }
            catch (FileNotFoundException ex)
            {
                throw new EngineRenderException("VoxCPM2 host output file was missing.", ex);
            }
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* best-effort */ }
            gate.Release();
        }
    }

    /// <summary>Clone the reference voice speaking <paramref name="text"/> and feed the PCM to
    /// <paramref name="sink"/> chunk-by-chunk as the host produces it, so playback can start before the line
    /// finishes. The host frames each chunk as a big-endian int32 length then that many PCM16 bytes; a
    /// 0-length frame ends the stream, a -1 length signals an error. Drains the whole stream (the sink decides
    /// whether to actually play) so the host never blocks on a full pipe.</summary>
    public async Task StreamAsync(string refWav, string text, string? style, IAudioSink sink, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            var proc = process!;
            await WriteRequestAsync(proc, new Request { Cmd = "stream", RefWav = refWav, Text = text, Style = style }, ct).ConfigureAwait(false);

            sink.Begin(SampleRate, 1); // host streams mono PCM16 at SampleRate
            var stdout = proc.StandardOutput.BaseStream;
            var readTask = Task.Run(() => ReadFrames(stdout, sink), CancellationToken.None);
            var completed = await Task.WhenAny(readTask, Task.Delay(RenderTimeout, CancellationToken.None)).ConfigureAwait(false);
            if (completed != readTask) { KillProcess(); throw new TimeoutException("VoxCPM2 host stream timed out."); }
            await readTask.ConfigureAwait(false); // observe exceptions (host-death / error frame)
        }
        catch (InvalidOperationException) { KillProcess(); throw; } // closed stdout mid-stream → host is dead
        finally { gate.Release(); }
    }

    // Reads length-prefixed PCM frames until the 0-length terminator. Throws InvalidOperationException if the
    // host closed stdout mid-stream (dead host), EngineRenderException on an explicit -1 error frame (host alive).
    private static void ReadFrames(Stream stdout, IAudioSink sink)
    {
        var lenBuf = new byte[4];
        while (true)
        {
            if (!ReadExact(stdout, lenBuf, 4)) throw new InvalidOperationException("VoxCPM2 host closed stdout mid-stream.");
            var len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (len == 0) return;                                       // clean end
            if (len < 0) throw new EngineRenderException("VoxCPM2 host reported a streaming error.");
            var data = new byte[len];
            if (!ReadExact(stdout, data, len)) throw new InvalidOperationException("VoxCPM2 host closed stdout mid-frame.");
            sink.Feed(data);
        }
    }

    private static bool ReadExact(Stream s, byte[] buf, int count)
    {
        var off = 0;
        while (off < count)
        {
            var n = s.Read(buf, off, count - off);
            if (n <= 0) return false; // EOF — pipe closed
            off += n;
        }
        return true;
    }

    private static async Task WriteRequestAsync(Process proc, Request req, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(req);
        await proc.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<string?> ReadSignalAsync(Process proc, TimeSpan timeout)
    {
        var stdout = proc.StandardOutput.BaseStream;
        var readTask = Task.Run(() => ReadLineRaw(stdout), CancellationToken.None);
        var completed = await Task.WhenAny(readTask, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
        if (completed != readTask) throw new TimeoutException("VoxCPM2 host request timed out.");
        return await readTask.ConfigureAwait(false);
    }

    // Reads one UTF-8 line (up to and excluding '\n') straight from the raw stdout pipe. Returns null at EOF.
    private static string? ReadLineRaw(Stream s)
    {
        var buf = new List<byte>(64);
        while (true)
        {
            var b = s.ReadByte();
            if (b < 0) return buf.Count == 0 ? null : Encoding.UTF8.GetString(buf.ToArray());
            if (b == '\n')
            {
                if (buf.Count > 0 && buf[^1] == (byte)'\r') buf.RemoveAt(buf.Count - 1);
                return Encoding.UTF8.GetString(buf.ToArray());
            }
            buf.Add((byte)b);
        }
    }

    private void EnsureStarted()
    {
        if (process is { HasExited: false }) return;
        process?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? ".",
        };
        psi.ArgumentList.Add(scriptPath);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["TQDM_DISABLE"] = "1";                    // silence VoxCPM2's per-step progress bar (log spam)
        psi.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";    // and HF download bars
        psi.Environment["VOXCPM_PARENT_PID"] = Environment.ProcessId.ToString(); // watchdog: exit if the game dies
        if (!string.IsNullOrEmpty(model)) psi.Environment["VOXCPM_MODEL"] = model;

        var p = new Process { StartInfo = psi };
        if (!p.Start()) throw new InvalidOperationException("Failed to start the VoxCPM2 host.");
        p.ErrorDataReceived += static (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            try { Plugin.Log.Information("[VoxCpmHost] " + e.Data); } catch { /* logging is best-effort */ }
        };
        p.BeginErrorReadLine();
        process = p;

        // Drain stdout (raw) to the "SR <hz>" handshake — also the load barrier. Skipping to the marker
        // (rather than reading one line) keeps stdout in sync if any load-time library output precedes "SR".
        var stdout = p.StandardOutput.BaseStream;
        var loadDeadline = DateTime.UtcNow + LoadTimeout;
        while (true)
        {
            var remaining = loadDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { KillProcess(); throw new TimeoutException("VoxCPM2 host load timed out."); }
            var lineTask = Task.Run(() => ReadLineRaw(stdout), CancellationToken.None);
            if (!lineTask.Wait(remaining)) { KillProcess(); throw new TimeoutException("VoxCPM2 host load timed out."); }
            var sr = lineTask.Result;
            if (sr == null) { KillProcess(); throw new InvalidOperationException("VoxCPM2 host closed stdout during load."); }
            if (sr.StartsWith("SR ", StringComparison.Ordinal))
            {
                if (int.TryParse(sr.AsSpan(3), out var hz)) SampleRate = hz;
                break;
            }
            try { Plugin.Log.Information("[VoxCpmHost] (pre-SR stdout) " + sr); } catch { /* best-effort */ }
        }
    }

    private void KillProcess()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { process?.Dispose(); } catch { /* ignore */ }
        process = null;
    }

    private static RenderedAudio ReadRendered(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return WavReader.ReadOne(fs);
            }
            catch (IOException) when (attempt < 10) { Thread.Sleep(50); }
        }
    }

    public void Dispose()
    {
        var acquired = gate.Wait(2000);
        try
        {
            if (process is { HasExited: false })
            {
                try { process.StandardInput.Close(); } catch { /* ignore */ }
            }
            KillProcess();
        }
        finally
        {
            if (acquired) { gate.Release(); gate.Dispose(); }
        }
    }

    private sealed class Request
    {
        [JsonPropertyName("cmd")] public string Cmd { get; init; } = "";
        [JsonPropertyName("desc")] public string? Desc { get; init; }
        [JsonPropertyName("refText")] public string? RefText { get; init; }
        [JsonPropertyName("refWav")] public string? RefWav { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("style")] public string? Style { get; init; }
        [JsonPropertyName("seed")] public int? Seed { get; init; }
        [JsonPropertyName("timesteps")] public int? Timesteps { get; init; }
        [JsonPropertyName("outputFile")] public string OutputFile { get; init; } = "";
    }
}
