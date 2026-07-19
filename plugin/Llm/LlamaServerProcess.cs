using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PopotoVox.Llm;

/// <summary>Process-level options for the local llama-server.</summary>
public sealed record LlmOptions(
    string ExePath,
    string ModelPath,
    int Port,
    int CtxSize,
    int NGpuLayers,
    int Threads);

/// <summary>
/// Owns the <c>llama-server.exe</c> child process (PRD D10 — upstream binary
/// launched as a child, loopback HTTP only). Started lazily, killed on dispose so
/// it can't be orphaned. Health/readiness is the client's concern (HTTP /health).
/// </summary>
internal sealed class LlamaServerProcess : IDisposable
{
    private readonly LlmOptions options;
    private readonly object gate = new();
    private Process? process;

    public LlamaServerProcess(LlmOptions options) => this.options = options;

    public string BaseUrl => $"http://127.0.0.1:{options.Port}";
    public bool IsRunning => process is { HasExited: false };

    public void StartIfNeeded()
    {
        lock (gate)
        {
            if (process is { HasExited: false }) return;
            process?.Dispose();

            var psi = new ProcessStartInfo
            {
                FileName = options.ExePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(options.ExePath),
            };
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(options.ModelPath);
            psi.ArgumentList.Add("--host"); psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(options.Port.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("--ctx-size"); psi.ArgumentList.Add(options.CtxSize.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-ngl"); psi.ArgumentList.Add(options.NGpuLayers.ToString(CultureInfo.InvariantCulture));
            if (options.Threads > 0)
            {
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(options.Threads.ToString(CultureInfo.InvariantCulture));
            }

            var p = new Process { StartInfo = psi };
            if (!p.Start())
                throw new InvalidOperationException("Failed to start llama-server.exe.");

            // Drain pipes so the server never stalls on a full stdout/stderr buffer.
            p.OutputDataReceived += static (_, _) => { };
            p.ErrorDataReceived += static (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            process = p;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            }
            process?.Dispose();
            process = null;
        }
    }
}
