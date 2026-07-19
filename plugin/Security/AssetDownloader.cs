using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using PopotoVox.Infrastructure;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;

namespace PopotoVox.Security;

/// <summary>
/// Fetches a manifest-named asset to a staging file, verifies its SHA-256 against
/// the pinned value, and only then moves/extracts it into the assets tree
/// (PRD §8.1 #4). Connects only to allowlisted hosts — and re-checks the host on
/// every redirect hop, not just the first URL. Untrusted text (model cards,
/// READMEs) can never influence any of this: the only inputs are the signed
/// manifest entry and the bytes on the wire, which must match the pinned hash.
/// </summary>
public sealed class AssetDownloader : IDisposable
{
    private const int MaxRedirects = 10;

    private readonly PluginPaths paths;
    private readonly AuditLog audit;
    private readonly HttpClient http;

    public AssetDownloader(PluginPaths paths, AuditLog audit)
    {
        this.paths = paths;
        this.audit = audit;

        // Redirects are followed manually so we can validate every hop's host.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        };
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PopotoVox/0.0.1");
    }

    /// <summary>Returns true if the asset is already installed and verifies clean.</summary>
    public async Task<bool> IsInstalledAsync(AssetEntry asset, CancellationToken ct = default)
    {
        if (asset.Archive != null)
        {
            // For archives we record a sentinel of the verified source hash.
            var marker = ArchiveMarkerPath(asset);
            if (!File.Exists(marker)) return false;
            var recorded = (await File.ReadAllTextAsync(marker, ct).ConfigureAwait(false)).Trim();
            return Hashing.HashEquals(asset.Sha256, recorded);
        }

        var target = InstallPathOf(asset);
        if (!File.Exists(target)) return false;
        var actual = await Hashing.Sha256FileAsync(target, ct).ConfigureAwait(false);
        return Hashing.HashEquals(asset.Sha256, actual);
    }

    public async Task DownloadAndInstallAsync(
        AssetEntry asset, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        // License is re-checked here even though the manifest gate already passed —
        // defense in depth, so no code path can install a restricted asset.
        if (!LicensePolicy.IsPermissive(asset.License))
        {
            audit.Write(asset.Id, asset.Url, asset.Sha256, null, "REFUSED_LICENSE");
            throw new InvalidOperationException($"Asset '{asset.Id}' has non-permissive license.");
        }

        var staging = Path.Combine(paths.Staging, asset.Id + ".part");
        try
        {
            progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Connecting, 0, asset.Size));
            await StreamToStagingAsync(asset, staging, progress, ct).ConfigureAwait(false);

            progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Verifying, asset.Size, asset.Size));
            var actual = await Hashing.Sha256FileAsync(staging, ct).ConfigureAwait(false);
            if (!Hashing.HashEquals(asset.Sha256, actual))
            {
                TryDelete(staging);
                audit.Write(asset.Id, asset.Url, asset.Sha256, actual, "HASH_MISMATCH");
                throw new InvalidOperationException(
                    $"Checksum mismatch for '{asset.Id}'. Expected {asset.Sha256}, got {actual}. Aborted.");
            }

            progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Installing, asset.Size, asset.Size));
            Install(asset, staging);

            audit.Write(asset.Id, asset.Url, asset.Sha256, actual, "OK");
            progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Done, asset.Size, asset.Size));
        }
        catch (OperationCanceledException)
        {
            audit.Write(asset.Id, asset.Url, asset.Sha256, null, "CANCELLED");
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            audit.Write(asset.Id, asset.Url, asset.Sha256, null, "ERROR: " + ex.Message);
            progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Failed, 0, asset.Size, ex.Message));
            throw;
        }
        catch (InvalidOperationException)
        {
            progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Failed, 0, asset.Size, "verification failed"));
            throw;
        }
    }

    private async Task StreamToStagingAsync(
        AssetEntry asset, string staging, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        long existing = File.Exists(staging) ? new FileInfo(staging).Length : 0;
        if (existing > asset.Size) { TryDelete(staging); existing = 0; } // corrupt partial

        using var response = await SendFollowingRedirectsAsync(asset.Url, existing, ct).ConfigureAwait(false);

        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        // If the server claims partial content but from a different offset than we asked,
        // don't trust the splice — restart from scratch (the hash would catch it anyway).
        if (append && response.Content.Headers.ContentRange?.From != existing)
            append = false;
        if (!append) existing = 0; // server ignored Range / desynced — restart cleanly

        var total = asset.Size;
        await using (var netStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(
            staging, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long received = existing;
            int read;
            while ((read = await netStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadProgress(asset.Id, DownloadPhase.Downloading, received, total));
            }
        }

        var finalSize = new FileInfo(staging).Length;
        if (finalSize != asset.Size)
        {
            TryDelete(staging);
            throw new InvalidOperationException(
                $"Size mismatch for '{asset.Id}'. Expected {asset.Size} bytes, got {finalSize}.");
        }
    }

    /// <summary>
    /// Issues a GET and follows redirects by hand, validating each hop's host
    /// against the allowlist. Returns the first non-redirect response.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        string startUrl, long resumeFrom, CancellationToken ct)
    {
        var url = new Uri(startUrl);
        for (var hop = 0; hop < MaxRedirects; hop++)
        {
            if (!HostAllowlist.IsAllowed(url))
                throw new InvalidOperationException($"Refused: host '{url.Host}' is not on the allowlist.");

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeFrom > 0)
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location != null)
            {
                var next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(url, response.Headers.Location);
                response.Dispose();
                url = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException($"Download of '{startUrl}' failed: HTTP {(int)code}.");
            }

            return response;
        }
        throw new InvalidOperationException($"Too many redirects fetching '{startUrl}'.");
    }

    private void Install(AssetEntry asset, string staging)
    {
        if (asset.Archive is "zip" or "tarbz2")
        {
            var dir = Path.Combine(paths.Assets, asset.InstallDir
                ?? throw new InvalidOperationException($"Asset '{asset.Id}' is an archive but has no installDir."));
            if (asset.Archive == "zip") ExtractZipSafely(staging, dir);
            else ExtractTarBz2Safely(staging, dir);
            File.WriteAllText(ArchiveMarkerPath(asset), asset.Sha256);
            TryDelete(staging);
        }
        else
        {
            var target = InstallPathOf(asset);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target)) File.Delete(target);
            File.Move(staging, target);
        }
    }

    /// <summary>Extracts a zip, rejecting any entry that would escape the target dir (zip-slip).</summary>
    private static void ExtractZipSafely(string zipPath, string targetDir)
    {
        // Extract over any existing contents (don't wipe) so multiple archives can share a
        // directory — e.g. the CUDA llama-server + its separate cudart zip.
        Directory.CreateDirectory(targetDir);

        var root = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // A rooted/absolute entry name makes Path.Combine discard targetDir entirely,
            // so reject it outright before resolving.
            if (Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':'))
                throw new InvalidOperationException($"Zip entry '{entry.FullName}' is rooted; refused.");

            var destination = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            // OrdinalIgnoreCase: the Windows filesystem is case-insensitive, so the
            // containment check must be too, or an escape could slip past on case.
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Zip entry '{entry.FullName}' escapes the target directory.");

            if (string.IsNullOrEmpty(entry.Name)) // directory entry
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>Extracts a .tar.bz2 with the same zip-slip / rooted-entry guards as the zip path.</summary>
    private static void ExtractTarBz2Safely(string archivePath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        var root = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
        using var fs = File.OpenRead(archivePath);
        using var bz = new BZip2InputStream(fs);
        using var tar = new TarInputStream(bz, System.Text.Encoding.UTF8);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.IsDirectory) continue;
            var name = entry.Name;
            if (string.IsNullOrEmpty(name)) continue;
            if (Path.IsPathRooted(name) || name.Contains(':'))
                throw new InvalidOperationException($"Archive entry '{name}' is rooted; refused.");

            var destination = Path.GetFullPath(Path.Combine(targetDir, name));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Archive entry '{name}' escapes the target directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var outFile = File.Create(destination);
            tar.CopyEntryContents(outFile);
        }
    }

    /// <summary>
    /// Removes an installed asset's payload. Plain files are deleted outright; for archives only the
    /// per-asset verified marker is removed here — the shared extract directory is reclaimed separately
    /// by <see cref="DeleteInstallDir"/> once no remaining asset still markers it.
    /// </summary>
    public void Uninstall(AssetEntry asset)
    {
        if (asset.Archive != null)
            TryDelete(ArchiveMarkerPath(asset));
        else
            TryDelete(InstallPathOf(asset));
    }

    /// <summary>True if the (archive) asset's verified marker is still present.</summary>
    public bool HasMarker(AssetEntry asset) => File.Exists(ArchiveMarkerPath(asset));

    /// <summary>Recursively delete an extract directory (named relative to the assets root).</summary>
    public void DeleteInstallDir(string installDir)
    {
        var dir = Path.Combine(paths.Assets, installDir);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best-effort reclaim */ }
    }

    private string InstallPathOf(AssetEntry asset) =>
        Path.Combine(paths.Assets, asset.InstallPath
            ?? throw new InvalidOperationException($"Asset '{asset.Id}' has no installPath."));

    // Per-asset marker (by id, not installDir) so two archives sharing a directory each
    // track their own verified state.
    private string ArchiveMarkerPath(AssetEntry asset) =>
        Path.Combine(paths.Assets, asset.Id + ".verified");

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    public void Dispose() => http.Dispose();
}
