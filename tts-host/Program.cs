using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SherpaOnnx;

// PopotoVox.TtsHost: load a Kokoro model once, then render one WAV per stdin request.
//
// Usage:  PopotoVox.TtsHost --model-dir <dir-with-model.onnx>
// Protocol (one JSON object per stdin line):
//   {"text":"...","speakerId":0,"speed":1.0,"outputFile":"C:\\...\\u1.wav"}
// On success it writes the WAV to outputFile and echoes that path on stdout.
// On a render error it echoes an empty line on stdout (the plugin treats that as "skip").
// Diagnostics go to stderr; "READY" on stderr signals the model finished loading.

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

var modelDir = GetArg(args, "--model-dir");
if (modelDir == null)
{
    Console.Error.WriteLine("ERR: --model-dir is required");
    return 2;
}

string P(string file) => Path.Combine(modelDir, file);

// A missing file passed to the native side faults instead of erroring — validate here.
foreach (var required in new[] { "model.onnx", "voices.bin", "tokens.txt", "lexicon-us-en.txt", "lexicon-zh.txt" })
{
    if (!File.Exists(P(required)))
    {
        Console.Error.WriteLine($"ERR: missing model file '{required}' in {modelDir}");
        return 3;
    }
}

OfflineTts tts;
try
{
    // Match the upstream sherpa multi-lang config EXACTLY: both lexicons, espeak data,
    // and crucially NO dict-dir (setting it faults the native loader).
    var config = new OfflineTtsConfig();
    config.Model.Kokoro.Model = P("model.onnx");
    config.Model.Kokoro.Voices = P("voices.bin");
    config.Model.Kokoro.Tokens = P("tokens.txt");
    config.Model.Kokoro.DataDir = P("espeak-ng-data");
    config.Model.Kokoro.Lexicon = P("lexicon-us-en.txt") + "," + P("lexicon-zh.txt");
    config.Model.NumThreads = 2;
    config.Model.Provider = "cpu";
    config.Model.Debug = 0;

    tts = new OfflineTts(config);
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERR: failed to initialize Kokoro: " + ex.Message);
    return 4;
}

Console.Error.WriteLine("READY"); // model loaded

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

string? line;
while ((line = Console.In.ReadLine()) != null)
{
    line = line.TrimStart('﻿').Trim(); // strip any stray BOM/whitespace
    if (line.Length == 0) continue;

    string outputPath = "";
    try
    {
        var req = JsonSerializer.Deserialize<RenderRequest>(line, jsonOptions);
        if (req == null || string.IsNullOrEmpty(req.OutputFile))
        {
            Console.Out.WriteLine();
            Console.Out.Flush();
            continue;
        }

        outputPath = req.OutputFile;
        var speed = req.Speed <= 0 ? 1.0f : req.Speed;
        var audio = tts.Generate(req.Text ?? "", speed, req.SpeakerId);
        audio.SaveToWaveFile(outputPath);

        Console.Out.WriteLine(outputPath); // completion signal
        Console.Out.Flush();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("ERR: render failed: " + ex.Message);
        Console.Out.WriteLine(); // empty = the plugin skips this line rather than waiting forever
        Console.Out.Flush();
    }
}

return 0;

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

internal sealed class RenderRequest
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("speakerId")] public int SpeakerId { get; set; }
    [JsonPropertyName("speed")] public float Speed { get; set; } = 1.0f;
    [JsonPropertyName("outputFile")] public string? OutputFile { get; set; }
}
