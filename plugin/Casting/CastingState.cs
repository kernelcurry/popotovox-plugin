using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PopotoVox.Casting;

/// <summary>
/// Observable state for the on-screen indicator (PRD D12). Tracks two phases the user
/// may be waiting on, both read by the indicator overlay each frame:
///   • <b>casting</b> — the one-time LLM voice assignment for a new NPC, and
///   • <b>rendering</b> — synthesizing the audio for a line (seconds on the GPU
///     engine, plus a one-time model warm-up on the first line).
/// </summary>
public sealed class CastingState
{
    private readonly ConcurrentDictionary<uint, string> casting = new();
    private readonly ConcurrentDictionary<long, Render> rendering = new();

    private readonly record struct Render(string Speaker, DateTime StartUtc);

    public void Begin(uint npcId, string speakerName) => casting[npcId] = speakerName;
    public void End(uint npcId) => casting.TryRemove(npcId, out _);

    public bool IsCasting(uint npcId) => casting.ContainsKey(npcId);
    public bool Any => !casting.IsEmpty;
    public IReadOnlyList<string> ActiveSpeakers => casting.Values.Distinct().ToList();

    /// <summary>Mark that a line for <paramref name="speaker"/> is being synthesized.</summary>
    public void BeginRender(long token, string speaker) => rendering[token] = new Render(speaker, DateTime.UtcNow);
    public void EndRender(long token) => rendering.TryRemove(token, out _);

    /// <summary>
    /// Renders that have been running at least <paramref name="minVisible"/> — the small delay
    /// keeps the indicator from flickering on fast engines (Kokoro/Piper finish sub-second),
    /// while still surfacing the GPU engine's multi-second renders and first-line warm-up.
    /// </summary>
    public IReadOnlyList<(string Speaker, double Seconds)> ActiveRenders(TimeSpan minVisible)
    {
        var now = DateTime.UtcNow;
        var list = new List<(string, double)>();
        foreach (var r in rendering.Values)
        {
            var elapsed = now - r.StartUtc;
            if (elapsed >= minVisible) list.Add((r.Speaker, elapsed.TotalSeconds));
        }
        return list;
    }

    // ---------------------------------------------------------------- live pipeline activity
    // A lightweight, observe-only stage tracker that powers the System tab's flow diagram.
    // The director stamps the current stage (+ who) as a line moves through; the UI reads the
    // last-seen time per stage so recently-active nodes can linger briefly. Purely diagnostic —
    // it never gates or changes the pipeline.

    public enum PipelineStage { Capturing, Casting, Annotating, Designing, Rendering, Playing }

    private readonly ConcurrentDictionary<PipelineStage, DateTime> stageAt = new();
    private volatile string? activitySpeaker;

    public void Mark(PipelineStage stage, string speaker)
    {
        stageAt[stage] = DateTime.UtcNow;
        activitySpeaker = speaker;
    }

    /// <summary>When the given stage was last active (UTC), or null if never.</summary>
    public DateTime? StageAt(PipelineStage stage) => stageAt.TryGetValue(stage, out var t) ? t : null;

    /// <summary>Who the most recent pipeline activity was for.</summary>
    public string? ActivitySpeaker => activitySpeaker;
}
