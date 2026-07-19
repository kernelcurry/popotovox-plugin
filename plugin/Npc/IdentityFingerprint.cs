using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PopotoVox.Npc;

/// <summary>
/// Computes the fallback identity key from PRD D13: <c>(normalizedName, modelHash)</c>.
/// This is what keeps the same logical character on the same voice across zones and
/// quest chains even when the game assigns it different <c>NpcId</c>s.
///
/// normalizedName lowercases and strips titles/punctuation; modelHash is a stable
/// digest over chosen appearance + equipment fields (from static ENpcBase data),
/// stable across gear refits but distinguishing look-alikes.
/// </summary>
public static partial class IdentityFingerprint
{
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var lowered = name.Trim().ToLowerInvariant();
        lowered = TitlePrefix().Replace(lowered, "");        // strip leading honorifics
        lowered = NonWord().Replace(lowered, " ").Trim();    // collapse punctuation
        lowered = Whitespace().Replace(lowered, " ");
        return lowered;
    }

    /// <summary>
    /// Builds the model-hash seed (PRD D13) from the REFIT-STABLE customize fields only:
    /// race / tribe / gender / body type / base model. It deliberately takes neither npcId nor
    /// equipment models — including either would make the same logical character hash differently
    /// across NpcIds or gear changes, defeating the cross-link. This is the single place the
    /// "stable subset" is defined (and unit-tested); <see cref="NpcResolver"/> calls it.
    /// </summary>
    public static string ModelHashSeed(uint raceRowId, uint tribeRowId, int gender, int bodyType, uint modelCharaRowId) =>
        new StringBuilder()
            .Append('|').Append(raceRowId)
            .Append('|').Append(tribeRowId)
            .Append('|').Append(gender)
            .Append('|').Append(bodyType)
            .Append('|').Append(modelCharaRowId)
            .ToString();

    /// <summary>Short, stable hex digest of the model-hash seed (appearance + equipment).</summary>
    public static string ModelHash(string modelHashSeed)
    {
        if (string.IsNullOrEmpty(modelHashSeed)) return "0";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(modelHashSeed));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars is plenty
    }

    public static string Compute(string name, string modelHashSeed) =>
        Normalize(name) + "|" + ModelHash(modelHashSeed);

    [GeneratedRegex(@"^(the|lord|lady|ser|master|mistress|chief|captain)\s+")]
    private static partial Regex TitlePrefix();

    [GeneratedRegex(@"[^\w\s]")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
