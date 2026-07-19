using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PopotoVox.Infrastructure;

namespace PopotoVox.Tts;

/// <summary>
/// Piper-backed <see cref="ITtsEngine"/> for v1. Resolves the installed binary and
/// model from the assets tree and renders through long-lived <see cref="PiperProcess"/>es.
///
/// Piper can only vary text + speaker_id per line — pace (length_scale) and noise are
/// fixed when the process starts. To still let the casting layer's mood→pace choice be
/// <em>audible</em>, we keep a tiny pool of processes, one per quantized pace "bucket"
/// (slow / normal / fast), and route each render to the bucket nearest its VoiceSpec.
/// That bounds memory (≤ <see cref="PaceBuckets"/>.Length model loads) while giving
/// real tonal variation. Noise params stay global; changing settings rebuilds the pool.
/// </summary>
public sealed class PiperEngine : ITtsEngine
{
    public const int SpeakerCount = 904; // en_US-libritts-high

    private const string ModelFileName = "en_US-libritts-high.onnx";

    // Representative paces. A spec's LengthScale snaps to the nearest of these.
    private static readonly float[] PaceBuckets = { 0.85f, 1.0f, 1.15f };

    private readonly string exePath;
    private readonly string modelPath;
    private readonly string tempDir;
    private readonly object gate = new();

    private readonly Dictionary<float, PiperProcess> pool = new();
    private VoiceParams globalParams;

    public PiperEngine(PluginPaths paths, VoiceParams globalParams)
    {
        exePath = Path.Combine(paths.PiperDir, "piper", "piper.exe");
        modelPath = Path.Combine(paths.Models, ModelFileName);
        tempDir = paths.PiperTemp;
        this.globalParams = globalParams;
    }

    public bool IsReady =>
        File.Exists(exePath) && File.Exists(modelPath) && File.Exists(modelPath + ".json");

    /// <summary>Swap the global noise params; the pool rebuilds on next render.</summary>
    public void UpdateGlobalParams(VoiceParams newParams)
    {
        lock (gate)
        {
            globalParams = newParams;
            foreach (var p in pool.Values) p.Dispose();
            pool.Clear();
        }
    }

    public Task<RenderedAudio> RenderAsync(string text, VoiceSpec spec, RenderContext? ctx = null, CancellationToken ct = default)
    {
        if (!IsReady)
            throw new InvalidOperationException("Piper engine is not installed yet.");

        var pace = NearestPace(spec.Params.LengthScale);
        PiperProcess proc;
        lock (gate)
        {
            if (!pool.TryGetValue(pace, out proc!))
            {
                var bucketParams = new VoiceParams
                {
                    LengthScale = pace,
                    NoiseScale = globalParams.NoiseScale,
                    NoiseW = globalParams.NoiseW,
                };
                proc = new PiperProcess(exePath, modelPath, tempDir, bucketParams);
                pool[pace] = proc;
            }
        }
        return proc.SynthesizeAsync(SpeechText.StripEmotionTags(SpeechText.Normalize(text)), spec.SpeakerId, ct);
    }

    private static float NearestPace(float lengthScale) =>
        PaceBuckets.OrderBy(b => MathF.Abs(b - lengthScale)).First();

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var p in pool.Values) p.Dispose();
            pool.Clear();
        }
    }
}
