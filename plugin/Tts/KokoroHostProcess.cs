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
/// Owns the isolated <c>PopotoVox.TtsHost.exe</c> child process that renders Kokoro
/// audio (PRD D10 crash isolation: a native TTS fault kills only this helper, never
/// the game). The host loads the model once and renders one WAV file per request,
/// so it stays fast. Protocol mirrors Piper: one JSON line in on stdin, a WAV file
/// out, the path echoed on stdout as the completion signal.
/// </summary>
internal sealed class KokoroHostProcess : IDisposable
{
    // First request pays the model-load cost (a few seconds); be generous, then tight.
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(60);

    private readonly string exePath;
    private readonly string modelDir;
    private readonly string nativeDir;
    private readonly string tempDir;

    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private static int counter;

    public KokoroHostProcess(string exePath, string modelDir, string nativeDir, string tempDir)
    {
        this.exePath = exePath;
        this.modelDir = modelDir;
        this.nativeDir = nativeDir;
        this.tempDir = tempDir;
    }

    public async Task<RenderedAudio> SynthesizeAsync(string text, int speakerId, float speed, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var outPath = Path.Combine(tempDir, $"k{Interlocked.Increment(ref counter)}.wav");
        try
        {
            EnsureStarted();
            var proc = process!;

            var line = JsonSerializer.Serialize(new HostRequest
            {
                Text = text,
                SpeakerId = speakerId,
                Speed = speed,
                OutputFile = outPath,
            });
            await proc.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            // Wait for the completion signal. We deliberately do NOT cancel the read on ct —
            // abandoning a half-read response would desync the next request. The render is
            // fast once warm; the director just drops the result if the line is stale.
            var readTask = Task.Run(() => proc.StandardOutput.ReadLine());
            var completed = await Task.WhenAny(readTask, Task.Delay(RenderTimeout, CancellationToken.None))
                .ConfigureAwait(false);
            if (completed != readTask)
            {
                KillProcess();
                throw new TimeoutException("Kokoro host render timed out.");
            }

            var signal = await readTask.ConfigureAwait(false);
            if (signal == null)
            {
                KillProcess();
                throw new InvalidOperationException("Kokoro host closed stdout before completing the render.");
            }
            if (signal.Length == 0)
                throw new InvalidOperationException("Kokoro host reported a render error.");

            return ReadRendered(outPath);
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* best-effort */ }
            gate.Release();
        }
    }

    private void EnsureStarted()
    {
        if (process is { HasExited: false }) return;
        process?.Dispose();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(exePath),
        };
        psi.ArgumentList.Add("--model-dir");
        psi.ArgumentList.Add(modelDir);
        psi.ArgumentList.Add("--native-dir");
        psi.ArgumentList.Add(nativeDir);

        var p = new Process { StartInfo = psi };
        if (!p.Start())
            throw new InvalidOperationException("Failed to start PopotoVox.TtsHost.exe.");

        // Drain stderr (model-load logs / "READY") so the pipe never stalls.
        p.ErrorDataReceived += static (_, _) => { };
        p.BeginErrorReadLine();

        process = p;
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
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
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
            if (acquired)
            {
                gate.Release();
                gate.Dispose();
            }
        }
    }

    private sealed class HostRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("text")] public string Text { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("speakerId")] public int SpeakerId { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("speed")] public float Speed { get; init; } = 1.0f;
        [System.Text.Json.Serialization.JsonPropertyName("outputFile")] public string OutputFile { get; init; } = "";
    }
}
