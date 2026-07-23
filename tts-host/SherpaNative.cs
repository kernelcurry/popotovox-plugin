using System.Runtime.InteropServices;

// Minimal P/Invoke binding for the sherpa-onnx C API — offline TTS (Kokoro) only.
//
// This replaces the org.k2fsa.sherpa.onnx NuGet package: the metapackage unconditionally
// depends on all 8 platform runtime packages, which put 9 new packages on the official
// Dalamud repo's review list. Instead, the two native libraries this binding needs
// (sherpa-onnx-c-api.dll + onnxruntime.dll) are downloaded by the plugin's signed,
// SHA-256-pinned asset manifest straight from the upstream k2-fsa release — the same
// pattern as the piper and llama-server binaries — and loaded from --native-dir.
//
// ABI CONTRACT: struct marshaling is POSITIONAL. Every layout below mirrors c-api.h at
// the pinned release v1.13.2 (verified 2026-07-22 against
// https://github.com/k2-fsa/sherpa-onnx/blob/v1.13.2/sherpa-onnx/c-api/c-api.h), and the
// numeric defaults mirror the upstream C# bindings (scripts/dotnet, Apache-2.0), which
// this file is derived from. All engine sub-configs must be laid out even though only
// Kokoro is used — the native side reads the config as one flat blob. RE-VERIFY EVERY
// STRUCT against c-api.h before re-pinning the native asset to a different version; the
// host also sanity-checks NumSpeakers/SampleRate after load so a drifted ABI fails
// loudly at startup instead of corrupting memory mid-render.
//
// Strings marshal as UTF-8 (upstream uses LPStr/ANSI; the native side decodes paths as
// UTF-8, so LPUTF8Str is strictly more correct for non-ASCII Windows profile paths).
// Unused string fields must be "" (empty), never null.

namespace SherpaOnnx;

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsVitsModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Model;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Lexicon;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Tokens;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DataDir;
    public float NoiseScale;
    public float NoiseScaleW;
    public float LengthScale;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DictDir;

    public OfflineTtsVitsModelConfig()
    {
        Model = ""; Lexicon = ""; Tokens = ""; DataDir = ""; DictDir = "";
        NoiseScale = 0.667f; NoiseScaleW = 0.8f; LengthScale = 1.0f;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsMatchaModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string AcousticModel;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Vocoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Lexicon;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Tokens;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DataDir;
    public float NoiseScale;
    public float LengthScale;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DictDir;

    public OfflineTtsMatchaModelConfig()
    {
        AcousticModel = ""; Vocoder = ""; Lexicon = ""; Tokens = ""; DataDir = ""; DictDir = "";
        NoiseScale = 0.667f; LengthScale = 1.0f;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsKokoroModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Model;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Voices;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Tokens;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DataDir;
    public float LengthScale;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DictDir;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Lexicon;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Lang;

    public OfflineTtsKokoroModelConfig()
    {
        Model = ""; Voices = ""; Tokens = ""; DataDir = ""; DictDir = ""; Lexicon = ""; Lang = "";
        LengthScale = 1.0f;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsKittenModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Model;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Voices;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Tokens;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DataDir;
    public float LengthScale;

    public OfflineTtsKittenModelConfig()
    {
        Model = ""; Voices = ""; Tokens = ""; DataDir = "";
        LengthScale = 1.0f;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsZipvoiceModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Tokens;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Encoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Decoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Vocoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DataDir;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Lexicon;
    public float FeatScale;
    public float TShift;
    public float TargetRms;
    public float GuidanceScale;

    public OfflineTtsZipvoiceModelConfig()
    {
        Tokens = ""; Encoder = ""; Decoder = ""; Vocoder = ""; DataDir = ""; Lexicon = "";
        FeatScale = 0.1f; TShift = 0.5f; TargetRms = 0.1f; GuidanceScale = 1.0f;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsPocketModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string LmFlow;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string LmMain;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Encoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Decoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string TextConditioner;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string VocabJson;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string TokenScoresJson;
    public int VoiceEmbeddingCacheCapacity;

    public OfflineTtsPocketModelConfig()
    {
        LmFlow = ""; LmMain = ""; Encoder = ""; Decoder = "";
        TextConditioner = ""; VocabJson = ""; TokenScoresJson = "";
        VoiceEmbeddingCacheCapacity = 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsSupertonicModelConfig
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string DurationPredictor;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string TextEncoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string VectorEstimator;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Vocoder;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string TtsJson;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string UnicodeIndexer;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string VoiceStyle;

    public OfflineTtsSupertonicModelConfig()
    {
        DurationPredictor = ""; TextEncoder = ""; VectorEstimator = "";
        Vocoder = ""; TtsJson = ""; UnicodeIndexer = ""; VoiceStyle = "";
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsModelConfig
{
    public OfflineTtsVitsModelConfig Vits;
    public int NumThreads;
    public int Debug;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string Provider;
    public OfflineTtsMatchaModelConfig Matcha;
    public OfflineTtsKokoroModelConfig Kokoro;
    public OfflineTtsKittenModelConfig Kitten;
    public OfflineTtsZipvoiceModelConfig Zipvoice;
    public OfflineTtsPocketModelConfig Pocket;
    public OfflineTtsSupertonicModelConfig Supertonic;

    public OfflineTtsModelConfig()
    {
        Vits = new OfflineTtsVitsModelConfig();
        NumThreads = 1;
        Debug = 0;
        Provider = "cpu";
        Matcha = new OfflineTtsMatchaModelConfig();
        Kokoro = new OfflineTtsKokoroModelConfig();
        Kitten = new OfflineTtsKittenModelConfig();
        Zipvoice = new OfflineTtsZipvoiceModelConfig();
        Pocket = new OfflineTtsPocketModelConfig();
        Supertonic = new OfflineTtsSupertonicModelConfig();
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct OfflineTtsConfig
{
    public OfflineTtsModelConfig Model;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string RuleFsts;
    public int MaxNumSentences;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string RuleFars;
    public float SilenceScale;

    public OfflineTtsConfig()
    {
        Model = new OfflineTtsModelConfig();
        RuleFsts = "";
        MaxNumSentences = 1;
        RuleFars = "";
        SilenceScale = 0.2f;
    }
}

/// <summary>Mirror of the native SherpaOnnxGeneratedAudio result struct (read-only view).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeGeneratedAudio
{
    public readonly IntPtr Samples;   // const float*
    public readonly int N;            // sample count
    public readonly int SampleRate;
}

/// <summary>
/// Loads the pinned native libraries from an explicit directory and exposes the six
/// C-API entry points the host needs. <see cref="Initialize"/> must be called before
/// any other member.
/// </summary>
public static class SherpaNative
{
    private const string Dll = "sherpa-onnx-c-api";
    private static IntPtr libHandle;

    /// <summary>
    /// Load the natives from <paramref name="nativeDir"/> by absolute path. onnxruntime.dll is
    /// loaded FIRST: when the OS loader then resolves sherpa-onnx-c-api.dll's import of that
    /// name, the already-loaded module wins — no PATH/AddDllDirectory mutation needed.
    /// </summary>
    public static void Initialize(string nativeDir)
    {
        NativeLibrary.Load(Path.Combine(nativeDir, "onnxruntime.dll"));
        libHandle = NativeLibrary.Load(Path.Combine(nativeDir, Dll + ".dll"));
        NativeLibrary.SetDllImportResolver(typeof(SherpaNative).Assembly,
            (name, _, _) => name == Dll ? libHandle : IntPtr.Zero);
    }

    [DllImport(Dll)] internal static extern IntPtr SherpaOnnxCreateOfflineTts(ref OfflineTtsConfig config);
    [DllImport(Dll)] internal static extern void SherpaOnnxDestroyOfflineTts(IntPtr tts);
    [DllImport(Dll)] internal static extern int SherpaOnnxOfflineTtsSampleRate(IntPtr tts);
    [DllImport(Dll)] internal static extern int SherpaOnnxOfflineTtsNumSpeakers(IntPtr tts);

    [DllImport(Dll)]
    internal static extern IntPtr SherpaOnnxOfflineTtsGenerate(
        IntPtr tts, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int sid, float speed);

    [DllImport(Dll)] internal static extern void SherpaOnnxDestroyOfflineTtsGeneratedAudio(IntPtr audio);

    [DllImport(Dll)]
    internal static extern int SherpaOnnxWriteWave(
        IntPtr samples, int n, int sampleRate, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename);
}

/// <summary>One rendered utterance; owns the native buffer. Dispose after saving.</summary>
public sealed class GeneratedAudio : IDisposable
{
    private IntPtr ptr;

    internal GeneratedAudio(IntPtr ptr) => this.ptr = ptr;

    public void SaveToWaveFile(string path)
    {
        var a = Marshal.PtrToStructure<NativeGeneratedAudio>(ptr);
        // Native convention: returns 1 on success.
        if (SherpaNative.SherpaOnnxWriteWave(a.Samples, a.N, a.SampleRate, path) != 1)
            throw new IOException($"SherpaOnnxWriteWave failed for '{path}'.");
    }

    public void Dispose()
    {
        if (ptr == IntPtr.Zero) return;
        SherpaNative.SherpaOnnxDestroyOfflineTtsGeneratedAudio(ptr);
        ptr = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    ~GeneratedAudio() => Dispose();
}

/// <summary>Thin wrapper exposing exactly the surface the host uses.</summary>
public sealed class OfflineTts : IDisposable
{
    private IntPtr handle;

    public OfflineTts(OfflineTtsConfig config)
    {
        handle = SherpaNative.SherpaOnnxCreateOfflineTts(ref config);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("SherpaOnnxCreateOfflineTts failed (bad config or model files).");
    }

    public int SampleRate => SherpaNative.SherpaOnnxOfflineTtsSampleRate(handle);
    public int NumSpeakers => SherpaNative.SherpaOnnxOfflineTtsNumSpeakers(handle);

    public GeneratedAudio Generate(string text, float speed, int speakerId)
    {
        var p = SherpaNative.SherpaOnnxOfflineTtsGenerate(handle, text, speakerId, speed);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("SherpaOnnxOfflineTtsGenerate failed.");
        return new GeneratedAudio(p);
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        SherpaNative.SherpaOnnxDestroyOfflineTts(handle);
        handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    ~OfflineTts() => Dispose();
}
