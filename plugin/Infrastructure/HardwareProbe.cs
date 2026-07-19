using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PopotoVox.Infrastructure;

/// <summary>
/// A light, best-effort read of the user's hardware for the setup wizard's recommendation: whether an
/// NVIDIA GPU is present (+ its name, VRAM and driver version, via <c>nvidia-smi</c>), the logical CPU
/// core count, and free disk space on the install drive. Everything is wrapped so a missing tool / odd
/// output simply reads as "unknown" — it never throws.
/// </summary>
public sealed class HardwareInfo
{
    public bool HasNvidiaGpu { get; init; }
    public string? GpuName { get; init; }
    public int GpuVramMb { get; init; }

    /// <summary>NVIDIA driver version, e.g. "560.94" (null if unknown). Heavy tiers need a recent driver.</summary>
    public string? GpuDriver { get; init; }

    public int CpuCores { get; init; }

    /// <summary>Free space (MB) on the drive PopotoVox installs assets to; 0 if it couldn't be read.</summary>
    public long FreeDiskMb { get; init; }
}

public static class HardwareProbe
{
    /// <summary>Probe off the UI thread; the result is cached by the caller. <paramref name="diskPath"/>
    /// is any path on the install drive (so free space is reported for where assets land).</summary>
    public static Task<HardwareInfo> DetectAsync(string? diskPath = null) => Task.Run(() => Detect(diskPath));

    private static HardwareInfo Detect(string? diskPath)
    {
        var (hasGpu, name, vram, driver) = ProbeNvidia();
        return new HardwareInfo
        {
            HasNvidiaGpu = hasGpu,
            GpuName = name,
            GpuVramMb = vram,
            GpuDriver = driver,
            CpuCores = Environment.ProcessorCount,
            FreeDiskMb = FreeDiskMb(diskPath),
        };
    }

    private static long FreeDiskMb(string? path)
    {
        try
        {
            var root = Path.GetPathRoot(string.IsNullOrEmpty(path) ? AppContext.BaseDirectory : path);
            if (string.IsNullOrEmpty(root)) return 0;
            return new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static (bool HasGpu, string? Name, int VramMb, string? Driver) ProbeNvidia()
    {
        try
        {
            // nvidia-smi ships in System32 with the NVIDIA driver; fall back to PATH.
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (!File.Exists(exe)) exe = "nvidia-smi";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, null, 0, null);

            var stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(); } catch { /* best effort */ }
                return (false, null, 0, null);
            }
            if (proc.ExitCode != 0) return (false, null, 0, null);

            // First GPU line, e.g. "NVIDIA GeForce RTX 3070, 8192, 560.94".
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return (false, null, 0, null);
            var parts = lines[0].Split(',');
            var name = parts[0].Trim();
            var vram = 0;
            if (parts.Length > 1) int.TryParse(parts[1].Trim(), out vram);
            var driver = parts.Length > 2 ? parts[2].Trim() : null;
            return (name.Length > 0, name.Length > 0 ? name : null, vram, string.IsNullOrEmpty(driver) ? null : driver);
        }
        catch
        {
            return (false, null, 0, null);
        }
    }
}
