using System;
using System.Security.Cryptography;

namespace PopotoVox.Security;

/// <summary>
/// Verifies the detached signature over the embedded asset manifest (PRD §8.1 #2).
///
/// The public key below is compiled into the plugin binary; the matching private
/// key signs the manifest in a secure release environment and never enters the
/// repo. A swapped or tampered manifest fails verification and the whole asset
/// subsystem refuses to start — there is no "try anyway" path.
///
/// Algorithm: ECDSA over the NIST P-256 curve, SHA-256 digest, signatures in the
/// ASN.1/DER (Rfc3279) sequence form openssl emits.
/// </summary>
public static class ManifestSignature
{
    // SPKI (SubjectPublicKeyInfo), base64. Rotated only by shipping a new build.
    private const string PublicKeySpkiBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEYt+R2AUl4H7dFIzsID/UW3TeqItE9yKfnbm1NqA1r6Ug5I5bHhWMwuldT2wFK6DGErOBRp7pTuLKXxOhMR6Wxw==";

    /// <summary>
    /// Returns true only if <paramref name="signatureDer"/> is a valid signature
    /// over the exact bytes of <paramref name="manifestBytes"/>. Any exception
    /// (malformed key, malformed signature) is treated as a failed verification.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signatureDer)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeySpkiBase64), out _);
            return ecdsa.VerifyData(
                manifestBytes,
                signatureDer,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
