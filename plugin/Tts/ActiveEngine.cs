using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PopotoVox.Casting;
using PopotoVox.Llm;

namespace PopotoVox.Tts;

/// <summary>
/// The whole active-engine identity as one immutable snapshot. Everything the pipeline needs to render a
/// line for the current engine travels together, so no reader can ever observe a NEW engine paired with a
/// STALE matcher/annotator — the swap publishes one of these atomically.
/// </summary>
/// <param name="Engine">The live TTS engine (owns its host process / GPU model).</param>
/// <param name="Id">The engine choice enum.</param>
/// <param name="EngineId">Lower-cased engine name, stamped into each <see cref="VoiceSpec"/> so cached specs
/// re-adapt when the engine changes (see <c>CastingDirector.AdaptToEngine</c>).</param>
/// <param name="Catalog">The engine-specific voice roster.</param>
/// <param name="Matcher">Casts against <paramref name="Catalog"/>.</param>
/// <param name="Annotator">Non-null only when the engine performs per-line emotion (Ultra).</param>
public sealed record EngineBinding(
    ITtsEngine Engine,
    TtsEngineChoice Id,
    string EngineId,
    SpeakerCatalog Catalog,
    VoiceMatcher Matcher,
    EmotionAnnotator? Annotator);

/// <summary>
/// Holds the active <see cref="EngineBinding"/> behind a single volatile reference so the engine can be
/// hot-swapped at runtime with no plugin reload. Readers take a short-lived <see cref="RenderLease"/> around
/// any host-touching render; a swap publishes a new binding, then waits for the OLD binding's in-flight
/// leases to drain before disposing the old engine — so an engine is never disposed under a live render.
///
/// A per-<see cref="Slot"/> refcount (not one global counter) lets the old and new engine be in-flight at
/// once, which is what allows the default swap to warm the new engine WHILE the old one keeps serving lines
/// (near-zero dead air). Only a hypothetical two-GPU-engine swap closes the gate and drops lines — see
/// <c>Plugin.SwapEngineAsync</c>.
/// </summary>
public sealed class ActiveEngine
{
    /// <summary>One published generation of the engine identity plus its live in-flight render count.</summary>
    public sealed class Slot
    {
        public EngineBinding Binding { get; internal init; } = null!;
        internal int InFlight;
    }

    private volatile Slot current;
    private volatile bool closed; // fallback path only: when true, new renders are dropped

    public ActiveEngine(EngineBinding initial) => current = new Slot { Binding = initial };

    /// <summary>A consistent snapshot of the active engine identity — one volatile read.</summary>
    public EngineBinding Current => current.Binding;

    /// <summary>The current slot (opaque handle for the swap to quiesce against).</summary>
    public Slot CurrentSlot => current;

    /// <summary>True for the whole duration of a transition — UI shows "switching…" and precompute idles.</summary>
    public volatile bool Swapping;

    /// <summary>Pins a binding for the lifetime of one render. Hold it across the render's awaits and
    /// <see cref="Exit"/> in a finally; while held, the binding's engine will not be disposed by a swap.</summary>
    public readonly struct RenderLease
    {
        private readonly Slot? slot;
        internal RenderLease(Slot slot) => this.slot = slot;
        public EngineBinding Binding => slot!.Binding;
        public void Exit() { if (slot != null) Interlocked.Decrement(ref slot.InFlight); }
    }

    /// <summary>
    /// Enter a render against the live engine. Returns false — caller drops the line — only while the gate is
    /// closed during a two-GPU-engine swap (never happens with today's single GPU engine). On success the
    /// returned lease pins the binding until <see cref="RenderLease.Exit"/>.
    /// </summary>
    public bool TryEnterRender(out RenderLease lease)
    {
        while (true)
        {
            if (closed) { lease = default; return false; }
            var slot = current;
            Interlocked.Increment(ref slot.InFlight);
            // Only hand out a lease if this slot is STILL the published one after the increment — otherwise a
            // swap published between the read and the increment, so back out and retry on the new slot. This
            // guarantees the retired slot never gains a lease after Publish, so QuiesceAsync draining its
            // in-flight count to zero means its engine is safe to dispose (no use-after-dispose).
            if (ReferenceEquals(slot, current) && !closed) { lease = new RenderLease(slot); return true; }
            Interlocked.Decrement(ref slot.InFlight);
        }
    }

    /// <summary>Publish a new engine identity. New renders read this immediately; renders that already
    /// entered keep their old slot's binding until they exit. Returns the OLD slot to quiesce against.</summary>
    public Slot Publish(EngineBinding next)
    {
        var old = current;
        current = new Slot { Binding = next };
        return old;
    }

    /// <summary>Wait (bounded) for a retired slot's in-flight renders to finish, so its engine can be disposed.
    /// On timeout the caller disposes anyway; the straggling render throws into its swallowing catch.</summary>
    public async Task QuiesceAsync(Slot slot, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref slot.InFlight) > 0 && sw.Elapsed < timeout)
            await Task.Delay(50).ConfigureAwait(false);
    }

    // --- Two-GPU-engine fallback only (unreachable with one GPU engine): close the gate, drain, then swap. ---
    public void CloseGate() => closed = true;
    public void OpenGate() => closed = false;
}
