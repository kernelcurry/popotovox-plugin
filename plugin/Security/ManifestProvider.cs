using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PopotoVox.Security;

/// <summary>
/// Loads the embedded asset manifest, but only after (1) its detached signature
/// verifies against the compiled-in public key and (2) every asset's license
/// passes the permissive allowlist. If either gate fails this throws and the
/// caller must treat the asset subsystem as unavailable — there is no fallback
/// that proceeds with an unverified manifest (PRD §8.1 #2, §10.3).
/// </summary>
public sealed class ManifestProvider
{
    private const string ManifestResource = "PopotoVox.Assets.Manifest.json";
    private const string SignatureResource = "PopotoVox.Assets.Manifest.json.sig";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public AssetManifest Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var manifestBytes = ReadResource(asm, ManifestResource);
        var signatureText = System.Text.Encoding.UTF8.GetString(ReadResource(asm, SignatureResource)).Trim();

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Manifest signature is not valid base64.");
        }

        if (!ManifestSignature.Verify(manifestBytes, signature))
            throw new InvalidOperationException(
                "Asset manifest signature verification FAILED. Refusing to use it (PRD §8.1).");

        var manifest = JsonSerializer.Deserialize<AssetManifest>(manifestBytes, JsonOptions)
            ?? throw new InvalidOperationException("Asset manifest deserialized to null.");

        LicensePolicy.AssertAllPermissive(manifest);
        return manifest;
    }

    private static byte[] ReadResource(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
