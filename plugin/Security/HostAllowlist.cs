using System;
using System.Collections.Generic;

namespace PopotoVox.Security;

/// <summary>
/// The downloader connects only to these hosts (PRD §8.1 #3). Matching is by
/// registrable-domain suffix so the well-known CDN/redirect targets are covered
/// without opening the door to look-alikes:
///   github.com            → release pages + objects.githubusercontent.com redirects
///   githubusercontent.com → raw + release-asset redirect targets
///   huggingface.co        → model pages + cdn-lfs-*.huggingface.co LFS redirects
///   xethub.hf.co          → cas-bridge.*.xethub.hf.co (HF Xet content backend)
///   cdn.hf.co             → us.aws.cdn.hf.co etc. (HF's AWS-backed CDN for non-Xet LFS files)
/// All of github.com, hf.co and huggingface.co are owned by GitHub / Hugging Face; integrity is
/// independently guaranteed by the manifest's per-file SHA-256 + exact-size pins regardless of CDN.
/// Every redirect hop is re-checked against this list, not just the first URL.
/// </summary>
public static class HostAllowlist
{
    private static readonly string[] AllowedSuffixes =
    {
        "github.com",
        "githubusercontent.com",
        "huggingface.co",
        "xethub.hf.co", // HF Xet content backend (cas-bridge.*.xethub.hf.co)
        "cdn.hf.co",    // HF AWS-backed CDN (us.aws.cdn.hf.co) for non-Xet LFS downloads
    };

    public static IReadOnlyList<string> Suffixes => AllowedSuffixes;

    public static bool IsAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            return false;

        // A trailing dot ("github.com.") is a valid FQDN that resolves identically;
        // normalize it so it can't sneak past the suffix match.
        var host = uri.Host.TrimEnd('.');
        foreach (var suffix in AllowedSuffixes)
        {
            if (host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
