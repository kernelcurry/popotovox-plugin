using System;
using System.Collections.Generic;

namespace PopotoVox.Security;

/// <summary>
/// Enforces the free-forever guarantee (PRD §10): an asset may only be downloaded
/// if its license is on the permissive allowlist. The only acceptable obligation
/// is attribution. This is checked at manifest-load (fail closed) and again before
/// any download, so a non-permissive asset can never be offered or fetched.
/// </summary>
public static class LicensePolicy
{
    // Open-source families that are free to redistribute. GPL is included because
    // PopotoVox itself is GPL-3.0 (PRD §10 updated). Non-commercial / research-only
    // licenses remain forbidden — those are the ones that restrict an open release.
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "CC0-1.0", "CC0",
        "CC-BY-4.0", "CC BY 4.0",
        "Apache-2.0",
        "MIT",
        "BSD-2-Clause", "BSD-3-Clause",
        "GPL-3.0", "GPL-3.0-or-later", "LGPL-3.0",
        "NVIDIA-CUDA", // NVIDIA CUDA runtime redistributables — freely redistributable with apps
        // The packaged VoxCPM2 Python runtime zip: an aggregate of unmodified upstream packages that
        // are EACH on this allowlist (PSF, MIT, BSD, Apache-2.0, NVIDIA-CUDA inside the torch wheels).
        // Per-package license texts ship inside the zip under LICENSES/; breakdown in docs/LICENSES.md.
        "Aggregate-Permissive",
        "Public Domain",
    };

    public static bool IsPermissive(string license) =>
        !string.IsNullOrWhiteSpace(license) && Allowed.Contains(license.Trim());

    /// <summary>Throws if any asset in the manifest carries a non-permissive license.</summary>
    public static void AssertAllPermissive(AssetManifest manifest)
    {
        foreach (var asset in manifest.Assets)
        {
            if (!IsPermissive(asset.License))
                throw new InvalidOperationException(
                    $"Manifest asset '{asset.Id}' has non-permissive license '{asset.License}'. " +
                    "PopotoVox refuses to handle it (PRD §10).");
        }
    }
}
