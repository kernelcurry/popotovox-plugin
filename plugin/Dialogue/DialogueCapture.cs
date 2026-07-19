using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace PopotoVox.Dialogue;

/// <summary>
/// Owns the lifetime of every <see cref="IDialogueSource"/> and exposes a
/// ring-buffered view of recently captured events for the diagnostics UI.
/// </summary>
public sealed class DialogueCapture : IDisposable
{
    private const int RecentCapacity = 100;

    private readonly IReadOnlyList<IDialogueSource> sources;
    private readonly IPluginLog log;
    private readonly ConcurrentQueue<DialogueEvent> recent = new();

    public IReadOnlyCollection<DialogueEvent> Recent => recent.ToArray();
    public IReadOnlyList<IDialogueSource> Sources => sources;
    public IReadOnlyDictionary<string, string?> SanityReport { get; private set; } =
        new Dictionary<string, string?>();

    public event Action<DialogueEvent>? Captured;

    public DialogueCapture(IEnumerable<IDialogueSource> sources, IPluginLog log)
    {
        this.sources = sources.ToList();
        this.log = log;
        foreach (var s in this.sources)
            s.Captured += OnCaptured;
    }

    public void Start()
    {
        var report = new Dictionary<string, string?>();
        foreach (var s in sources)
        {
            if (!s.SanityCheck(out var error))
            {
                report[s.Name] = error;
                log.Warning($"[Dialogue] Source '{s.Name}' failed sanity check: {error}");
                continue;
            }
            report[s.Name] = null;
            try
            {
                s.Start();
                log.Information($"[Dialogue] Source '{s.Name}' started.");
            }
            catch (Exception ex)
            {
                report[s.Name] = ex.Message;
                log.Error(ex, $"[Dialogue] Source '{s.Name}' threw on Start().");
            }
        }
        SanityReport = report;
    }

    public void Stop()
    {
        foreach (var s in sources)
        {
            try { s.Stop(); }
            catch (Exception ex) { log.Error(ex, $"[Dialogue] Source '{s.Name}' threw on Stop()."); }
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (var s in sources)
        {
            s.Captured -= OnCaptured;
            try { s.Dispose(); } catch { /* swallow on teardown */ }
        }
    }

    private void OnCaptured(DialogueEvent e)
    {
        recent.Enqueue(e);
        while (recent.Count > RecentCapacity && recent.TryDequeue(out _)) { }
        log.Debug($"[Dialogue] {e}");
        Captured?.Invoke(e);
    }
}
