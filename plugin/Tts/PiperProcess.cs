using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PopotoVox.Tts;

/// <summary>
/// Owns one long-lived <c>piper.exe</c> child process and talks to it over stdin
/// (one JSON line per utterance). The process is started lazily, restarted if it
/// dies, and killed on dispose so it can never be orphaned (PRD risk: "helper
/// orphaned on crash/unload").
///
/// IMPORTANT: each utterance is rendered to a temp WAV <em>file</em>, not to stdout.
/// On Windows piper.exe writes stdout in text mode, which rewrites every 0x0A byte
/// of raw PCM to 0x0D 0x0A and turns the audio into static. So we give piper a
/// per-line "output_file" (a clean binary write) and use the file path it echoes
/// back on stdout purely as the per-utterance completion signal.
///
/// Per PRD D10 this is just the upstream Piper binary launched as a child — no
/// runtime is shipped or embedded.
/// </summary>
internal sealed class PiperProcess : IDisposable
{
    private readonly string exePath;
    private readonly string modelPath;
    private readonly string tempDir;
    private readonly VoiceParams globalParams;

    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;

    // Static so temp-file names are unique across ALL piper processes (the pace pool
    // runs several), not just within one — otherwise two buckets collide on "u1.wav".
    private static int counter;

    public PiperProcess(string exePath, string modelPath, string tempDir, VoiceParams globalParams)
    {
        this.exePath = exePath;
        this.modelPath = modelPath;
        this.tempDir = tempDir;
        this.globalParams = globalParams;
    }

    public async Task<RenderedAudio> SynthesizeAsync(string text, int speakerId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var outPath = Path.Combine(tempDir, $"u{Interlocked.Increment(ref counter)}.wav");
        try
        {
            EnsureStarted();
            var proc = process!;

            // One JSON object per line; piper renders to outPath (clean binary file)
            // and echoes that path on stdout when done.
            var line = JsonSerializer.Serialize(
                new PiperRequest { Text = text, SpeakerId = speakerId, OutputFile = outPath });
            await proc.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            // Wait for the completion line. ReadLineAsync(ct) is cancelable, but the
            // underlying pipe read isn't always promptly interruptible, so also race a
            // timeout that kills+restarts piper rather than wedging the gate forever.
            var signalTask = proc.StandardOutput.ReadLineAsync(ct).AsTask();
            var completed = await Task.WhenAny(signalTask, Task.Delay(RenderTimeout, ct)).ConfigureAwait(false);
            if (completed != signalTask)
            {
                KillProcess();
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException("Piper render timed out.");
            }

            var signal = await signalTask.ConfigureAwait(false);
            if (signal == null)
            {
                KillProcess(); // piper exited / closed stdout
                throw new InvalidOperationException("Piper closed stdout before completing the render.");
            }

            return ReadRendered(outPath);
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* best-effort */ }
            gate.Release();
        }
    }

    private static RenderedAudio ReadRendered(string path)
    {
        // The file is normally ready the instant piper echoes its path, but allow a
        // brief settle window in case the handle hasn't fully flushed yet.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return WavReader.ReadOne(fs);
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    private void KillProcess()
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { process?.Dispose(); } catch { /* ignore */ }
        process = null;
    }

    private void EnsureStarted()
    {
        if (process is { HasExited: false })
            return;

        process?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),  // Piper expects UTF-8 JSON lines
            StandardOutputEncoding = new UTF8Encoding(false), // stdout now carries the echoed path (text), not audio
            WorkingDirectory = Path.GetDirectoryName(exePath),
        };
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelPath);
        psi.ArgumentList.Add("--json_input"); // each line names its own output_file
        psi.ArgumentList.Add("--length_scale"); psi.ArgumentList.Add(Fmt(globalParams.LengthScale));
        psi.ArgumentList.Add("--noise_scale"); psi.ArgumentList.Add(Fmt(globalParams.NoiseScale));
        psi.ArgumentList.Add("--noise_w"); psi.ArgumentList.Add(Fmt(globalParams.NoiseW));

        var p = new Process { StartInfo = psi };
        if (!p.Start())
            throw new InvalidOperationException("Failed to start piper.exe.");

        // Drain stderr so the pipe never fills and stalls the process.
        p.ErrorDataReceived += static (_, _) => { };
        p.BeginErrorReadLine();

        process = p;
    }

    private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        // Bounded wait so a hung render (e.g. piper stalled) can't deadlock unload.
        var acquired = gate.Wait(2000);
        try
        {
            KillProcess();
        }
        finally
        {
            // Only release/dispose the gate if we actually took it — releasing an
            // un-acquired semaphore throws. On the timeout path we let it leak (unload).
            if (acquired)
            {
                gate.Release();
                gate.Dispose();
            }
        }
    }

    private sealed class PiperRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; init; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("speaker_id")]
        public int SpeakerId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("output_file")]
        public string OutputFile { get; init; } = "";
    }
}
