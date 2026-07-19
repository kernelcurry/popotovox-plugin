using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PopotoVox.Security;

/// <summary>
/// SHA-256 helpers. Streamed so we never hold a multi-gigabyte model in memory
/// just to hash it.
/// </summary>
public static class Hashing
{
    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Constant-time comparison so a hash check can't be timing-probed.</summary>
    public static bool HashEquals(string expectedHex, string actualHex)
    {
        if (expectedHex.Length != actualHex.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expectedHex),
            Convert.FromHexString(actualHex));
    }
}
