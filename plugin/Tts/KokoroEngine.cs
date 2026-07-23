using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PopotoVox.Infrastructure;

namespace PopotoVox.Tts;

/// <summary>
/// Kokoro voice engine, driven through the isolated <see cref="KokoroHostProcess"/>
/// helper. The plugin itself contains NO native TTS code, so a fault in the voice
/// engine can never crash the game (PRD D10). More natural than Piper, fully offline
/// on CPU, with per-call pace and speaker.
/// </summary>
public sealed class KokoroEngine : ITtsEngine
{
    public const int SpeakerCount = 53; // kokoro-multi-lang-v1_0 (53 speakers across 9 accents)

    /// <summary>The model subdir the current manifest installs; preferred over any stale sibling.</summary>
    private const string ModelDirName = "kokoro-multi-lang-v1_0";

    /// <summary>The native-libs subdir the current "sherpa-native" manifest asset extracts to;
    /// preferred over any stale sibling from an earlier pin.</summary>
    private const string NativeDirName = "sherpa-onnx-v1.13.2-win-x64-shared-MT-Release-lib";

    private readonly string hostExe;
    private readonly string kokoroRoot;
    private readonly string sherpaRoot;
    private readonly string tempDir;
    private readonly object gate = new();

    private KokoroHostProcess? host;
    private string? modelDir;
    private string? nativeLibDir;

    public KokoroEngine(PluginPaths paths, string pluginAssemblyDir)
    {
        hostExe = Path.Combine(pluginAssemblyDir, "tts-host", "PopotoVox.TtsHost.exe");
        kokoroRoot = paths.KokoroDir;
        sherpaRoot = paths.SherpaDir;
        tempDir = paths.PiperTemp;
    }

    /// <summary>Ready once the helper executable, the model, and the sherpa natives are all installed.</summary>
    public bool IsReady => File.Exists(hostExe) && FindModelDir() != null && FindNativeDir() != null;

    public Task<RenderedAudio> RenderAsync(string text, VoiceSpec spec, RenderContext? ctx = null, CancellationToken ct = default)
    {
        var dir = FindModelDir()
            ?? throw new InvalidOperationException("Kokoro model is not installed yet.");
        var natives = FindNativeDir()
            ?? throw new InvalidOperationException("Kokoro speech libraries (sherpa-native) are not installed yet.");
        if (!File.Exists(hostExe))
            throw new InvalidOperationException("Kokoro voice helper is missing.");

        var sid = ((spec.SpeakerId % SpeakerCount) + SpeakerCount) % SpeakerCount;
        // sherpa "speed" is the inverse of Piper-style length scale (higher = faster).
        var speed = Math.Clamp(1.0f / Math.Max(0.5f, spec.Params.LengthScale), 0.5f, 2.0f);

        KokoroHostProcess proc;
        lock (gate)
        {
            host ??= new KokoroHostProcess(hostExe, dir, natives, tempDir);
            proc = host;
        }
        return proc.SynthesizeAsync(SpeechText.StripEmotionTags(SpeechText.Normalize(text)), sid, speed, ct);
    }

    private string? FindModelDir()
    {
        if (modelDir != null && File.Exists(Path.Combine(modelDir, "model.onnx"))) return modelDir;
        if (!Directory.Exists(kokoroRoot)) return null;

        // The .tar.bz2 extracts to a versioned subdir. Prefer the model the current manifest
        // installs (ModelDirName) so a leftover older model (e.g. a previous kokoro-multi-lang
        // version with a different speaker count) can't win the nondeterministic enumeration.
        var dirs = Directory.EnumerateDirectories(kokoroRoot)
            .Where(d => File.Exists(Path.Combine(d, "model.onnx")))
            .ToList();
        modelDir = dirs.FirstOrDefault(d =>
                       string.Equals(Path.GetFileName(d), ModelDirName, StringComparison.OrdinalIgnoreCase))
                   ?? dirs.FirstOrDefault();

        // Best-effort: reclaim disk by removing stale sibling model dirs (old versions).
        if (modelDir != null)
        {
            foreach (var d in dirs)
            {
                if (d == modelDir) continue;
                try { Directory.Delete(d, recursive: true); } catch { /* non-fatal */ }
            }
        }
        return modelDir;
    }

    /// <summary>
    /// Locate the <c>lib/</c> directory of the installed sherpa-onnx natives. Same shape as
    /// <see cref="FindModelDir"/>: the .tar.bz2 extracts to a versioned subdir; prefer the one
    /// the current manifest installs so a leftover older pin can't win, and reclaim stale siblings.
    /// </summary>
    private string? FindNativeDir()
    {
        if (nativeLibDir != null && File.Exists(Path.Combine(nativeLibDir, "sherpa-onnx-c-api.dll"))) return nativeLibDir;
        if (!Directory.Exists(sherpaRoot)) return null;

        static bool HasLibs(string d) =>
            File.Exists(Path.Combine(d, "lib", "sherpa-onnx-c-api.dll")) &&
            File.Exists(Path.Combine(d, "lib", "onnxruntime.dll"));

        var dirs = Directory.EnumerateDirectories(sherpaRoot).Where(HasLibs).ToList();
        var chosen = dirs.FirstOrDefault(d =>
                         string.Equals(Path.GetFileName(d), NativeDirName, StringComparison.OrdinalIgnoreCase))
                     ?? dirs.FirstOrDefault();

        if (chosen != null)
        {
            foreach (var d in dirs)
            {
                if (d == chosen) continue;
                try { Directory.Delete(d, recursive: true); } catch { /* non-fatal */ }
            }
            nativeLibDir = Path.Combine(chosen, "lib");
        }
        return nativeLibDir;
    }

    public void Dispose()
    {
        lock (gate)
        {
            host?.Dispose();
            host = null;
        }
    }
}
