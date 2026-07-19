using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PopotoVox.Security;

/// <summary>
/// UI-facing wrapper around <see cref="AssetService"/>: runs group downloads on a
/// background task and exposes per-asset progress + install state for an
/// immediate-mode UI to poll each frame. Implements IProgress directly so updates
/// land without needing a synchronization context.
/// </summary>
public sealed class DownloadCoordinator : IProgress<DownloadProgress>, IDisposable
{
    private readonly AssetService assets;
    private readonly IPluginLog log;

    private readonly ConcurrentDictionary<string, DownloadProgress> progress = new();
    private readonly ConcurrentDictionary<string, bool> installed = new();
    private CancellationTokenSource? cts;

    public bool Busy { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Fires (on a background thread) after any download or removal batch finishes and the
    /// installed map is refreshed — the hook for reacting to a runtime appearing/disappearing on disk
    /// (e.g. relaunching the caster LLM on the CUDA build right after it installs).</summary>
    public event Action? BatchCompleted;

    public DownloadCoordinator(AssetService assets, IPluginLog log)
    {
        this.assets = assets;
        this.log = log;
        _ = RefreshInstalledAsync();
    }

    public bool? IsInstalled(string assetId) => installed.TryGetValue(assetId, out var v) ? v : null;
    public DownloadProgress? ProgressFor(string assetId) => progress.TryGetValue(assetId, out var p) ? p : null;

    public void Report(DownloadProgress value) => progress[value.AssetId] = value;

    public async Task RefreshInstalledAsync()
    {
        foreach (var asset in assets.Manifest.Assets)
        {
            try { installed[asset.Id] = await assets.IsInstalledAsync(asset).ConfigureAwait(false); }
            catch { installed[asset.Id] = false; }
        }
    }

    public void StartDownload(IEnumerable<Security.AssetEntry> group)
    {
        if (Busy) return;
        Busy = true;
        LastError = null;
        cts = new CancellationTokenSource();
        var list = group.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                await assets.EnsureAsync(list, this, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LastError = "Cancelled.";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.Error(ex, "[Assets] Download group failed.");
            }
            finally
            {
                await RefreshInstalledAsync().ConfigureAwait(false);
                Busy = false;
                NotifyBatchCompleted();
            }
        });
    }

    /// <summary>
    /// Uninstalls a group's assets on a background task, skipping <paramref name="protectedIds"/>
    /// (assets still needed by an in-use feature), then refreshes install state. Mirrors
    /// <see cref="StartDownload"/>; no-op while a download/removal is already running.
    /// </summary>
    public void RemoveGroup(IEnumerable<AssetEntry> group, ISet<string> protectedIds)
    {
        if (Busy) return;
        Busy = true;
        LastError = null;
        var list = group.ToList();

        _ = Task.Run(async () =>
        {
            try
            {
                assets.Remove(list, protectedIds);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.Error(ex, "[Assets] Remove group failed.");
            }
            finally
            {
                await RefreshInstalledAsync().ConfigureAwait(false);
                Busy = false;
                NotifyBatchCompleted();
            }
        });
    }

    private void NotifyBatchCompleted()
    {
        try { BatchCompleted?.Invoke(); }
        catch (Exception ex) { log.Warning(ex, "[Assets] BatchCompleted handler failed."); }
    }

    public void Cancel() => cts?.Cancel();

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}
