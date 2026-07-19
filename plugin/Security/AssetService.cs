using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PopotoVox.Infrastructure;

namespace PopotoVox.Security;

/// <summary>
/// The single front door to the asset subsystem: verifies + loads the manifest,
/// reports per-asset install state to the UI, and downloads groups (TTS / LLM) on
/// demand with progress. Everything underneath enforces the §8.1 guarantees.
/// </summary>
public sealed class AssetService : IDisposable
{
    private readonly AssetDownloader downloader;
    private readonly IPluginLog log;

    public AssetManifest Manifest { get; }
    public bool Available { get; }
    public string? UnavailableReason { get; }

    public AssetService(PluginPaths paths, IPluginLog log)
    {
        this.log = log;
        AssetManifest manifest;
        try
        {
            manifest = new ManifestProvider().Load();
            Available = true;
        }
        catch (Exception ex)
        {
            // A failed signature/license gate makes the whole subsystem unavailable
            // rather than proceeding with an unverified manifest.
            log.Error(ex, "[Assets] Manifest verification failed; asset downloads disabled.");
            manifest = new AssetManifest();
            Available = false;
            UnavailableReason = ex.Message;
        }

        Manifest = manifest;
        downloader = new AssetDownloader(paths, new AuditLog(paths.AuditLogPath));
    }

    /// <summary>The assets a given voice engine needs (from the engine registry).</summary>
    public IEnumerable<AssetEntry> AssetsForEngine(TtsEngineChoice id)
    {
        var ids = Tts.TtsEngineCatalog.Get(id).AssetIds;
        return Manifest.Assets.Where(a => ids.Contains(a.Id));
    }

    /// <summary>The caster-LLM assets: CPU runtime + model, plus the CUDA runtime build when
    /// <paramref name="includeCuda"/> (machines with an NVIDIA GPU — the casting/emotion LLM runs
    /// an order of magnitude faster there, so GPU users get it as part of the normal download).</summary>
    public IEnumerable<AssetEntry> LlmAssets(bool includeCuda = false) => Manifest.Assets.Where(a =>
        a.Kind is AssetKind.LlmRuntime or AssetKind.LlmModel
        || (includeCuda && a.Kind is AssetKind.LlmRuntimeCuda));

    /// <summary>The full download bundle for an engine: its own assets plus the caster LLM's when smart
    /// casting is used (including the CUDA build on GPU machines). The single source of truth for setup,
    /// the Settings install-check, and the live engine transition, so they never disagree about what
    /// "installed" means.</summary>
    public List<AssetEntry> BundleForEngine(TtsEngineChoice id, bool withLlm, bool withCudaLlm = false)
    {
        var list = AssetsForEngine(id).ToList();
        if (withLlm)
            foreach (var a in LlmAssets(withCudaLlm))
                if (list.All(x => x.Id != a.Id))
                    list.Add(a);
        return list;
    }

    public AssetEntry? Find(string id) => Manifest.Assets.FirstOrDefault(a => a.Id == id);

    public Task<bool> IsInstalledAsync(AssetEntry asset, CancellationToken ct = default) =>
        downloader.IsInstalledAsync(asset, ct);

    public async Task<bool> IsGroupInstalledAsync(IEnumerable<AssetEntry> group, CancellationToken ct = default)
    {
        foreach (var a in group)
            if (!await downloader.IsInstalledAsync(a, ct).ConfigureAwait(false))
                return false;
        return true;
    }

    /// <summary>Downloads any missing assets in the group, in manifest order, with progress.</summary>
    public async Task EnsureAsync(
        IEnumerable<AssetEntry> group, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!Available)
            throw new InvalidOperationException("Asset subsystem unavailable: " + UnavailableReason);

        foreach (var asset in group)
        {
            ct.ThrowIfCancellationRequested();
            if (await downloader.IsInstalledAsync(asset, ct).ConfigureAwait(false))
                continue;
            log.Information($"[Assets] Downloading '{asset.Id}' ({asset.Size / (1024 * 1024)} MB)…");
            await downloader.DownloadAndInstallAsync(asset, progress, ct).ConfigureAwait(false);
            log.Information($"[Assets] '{asset.Id}' installed and verified.");
        }
    }

    /// <summary>
    /// Removes the installed payload for a group, skipping any asset id in <paramref name="protectedIds"/>
    /// (assets a still-active feature needs — e.g. the shared CUDA runtime while smart casting is on).
    /// An archive's extract directory is reclaimed only once no remaining asset still markers it.
    /// </summary>
    public void Remove(IEnumerable<AssetEntry> group, ISet<string> protectedIds)
    {
        var toRemove = group.Where(a => !protectedIds.Contains(a.Id)).ToList();
        foreach (var a in toRemove)
            downloader.Uninstall(a);

        foreach (var dir in toRemove
                     .Where(a => a.Archive != null && a.InstallDir != null)
                     .Select(a => a.InstallDir!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stillUsed = Manifest.Assets.Any(a =>
                a.Archive != null &&
                string.Equals(a.InstallDir, dir, StringComparison.OrdinalIgnoreCase) &&
                downloader.HasMarker(a));
            if (!stillUsed)
                downloader.DeleteInstallDir(dir);
        }
    }

    public void Dispose() => downloader.Dispose();
}
