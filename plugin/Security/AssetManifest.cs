using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PopotoVox.Security;

/// <summary>
/// What an asset is for — drives consent tiering and where it installs.
/// </summary>
public enum AssetKind
{
    TtsEngine,        // Piper binary
    TtsModel,         // Piper voice model
    TtsModelConfig,   // Piper voice config
    KokoroModel,      // sherpa-onnx Kokoro model bundle (.tar.bz2)
    KokoroRuntime,    // sherpa-onnx native libraries (win-x64 shared, static CRT) the TTS host P/Invokes
    LlmRuntime,       // CPU llama-server (the casting/emotion LLM)
    LlmRuntimeCuda,   // CUDA llama-server + cudart — runs the casting/emotion LLM on GPU
    LlmModel,         // the casting/emotion GGUF
    VoxRuntime,       // portable Python runtime for the VoxCPM2 (Ultra) engine
    VoxModel,         // the VoxCPM2 model snapshot (Ultra), loaded fully offline from disk
}

/// <summary>
/// The pinned, signature-protected list of everything PopotoVox will ever
/// download (PRD §8.1 #1). Deserialized from the embedded <c>Assets/Manifest.json</c>
/// only after its detached signature verifies. The downloader trusts nothing that
/// is not described here — no discovery, no mirror-hopping, no user-supplied URLs.
/// </summary>
public sealed class AssetManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("pluginVersion")] public string PluginVersion { get; init; } = "";
    [JsonPropertyName("assets")] public IReadOnlyList<AssetEntry> Assets { get; init; } = new List<AssetEntry>();
}

public sealed class AssetEntry
{
    /// <summary>Stable identifier used in code and the audit log.</summary>
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    [JsonPropertyName("kind")] public AssetKind Kind { get; init; }

    /// <summary>Consent tier label from PRD §8 (e.g. "1a", "2", "3").</summary>
    [JsonPropertyName("tier")] public string Tier { get; init; } = "";

    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("size")] public long Size { get; init; }

    /// <summary>Lowercase hex SHA-256 the downloaded bytes must match exactly.</summary>
    [JsonPropertyName("sha256")] public string Sha256 { get; init; } = "";

    /// <summary>"zip" if the asset is an archive to extract; null for a plain file.</summary>
    [JsonPropertyName("archive")] public string? Archive { get; init; }

    /// <summary>SPDX-ish license id; must pass <see cref="LicensePolicy"/>.</summary>
    [JsonPropertyName("license")] public string License { get; init; } = "";

    /// <summary>Human-readable attribution shown in the Acknowledgements view.</summary>
    [JsonPropertyName("attribution")] public string? Attribution { get; init; }

    /// <summary>For plain files: install path relative to the assets root.</summary>
    [JsonPropertyName("installPath")] public string? InstallPath { get; init; }

    /// <summary>For archives: directory (relative to assets root) to extract into.</summary>
    [JsonPropertyName("installDir")] public string? InstallDir { get; init; }
}
