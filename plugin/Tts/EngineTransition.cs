using System;

namespace PopotoVox.Tts;

/// <summary>The step a voice-engine transition (boot warm-up or an Apply-driven swap) is on.</summary>
public enum TransitionPhase
{
    Idle,
    Downloading, // fetching the target engine's (and caster LLM's) assets
    Warming,     // spinning up the engine's host process / loading its model
    Swapping,    // publishing the new engine + draining the old
    Ready,       // done — the new engine is live
    Failed,      // aborted (e.g. not installed / download failed) — the previous engine keeps running
}

/// <summary>
/// Observable state for the shared boot/Apply progress window. Written by the transition task, polled each
/// frame by <c>EngineProgressWindow</c> (immediate-mode, so a torn read just self-corrects next frame).
/// </summary>
public sealed class EngineTransition
{
    public TtsEngineChoice Target { get; private set; }
    public TransitionPhase Phase { get; private set; } = TransitionPhase.Idle;
    public string? Error { get; private set; }
    public DateTime CompletedAt { get; private set; }

    /// <summary>A transition is under way (download / warm / swap in progress).</summary>
    public bool Active => Phase is TransitionPhase.Downloading or TransitionPhase.Warming or TransitionPhase.Swapping;

    public void Begin(TtsEngineChoice target, TransitionPhase phase)
    {
        Target = target;
        Phase = phase;
        Error = null;
    }

    public void Complete()
    {
        Phase = TransitionPhase.Ready;
        CompletedAt = DateTime.UtcNow;
    }

    public void Fail(string error)
    {
        Phase = TransitionPhase.Failed;
        Error = error;
        CompletedAt = DateTime.UtcNow;
    }
}
