using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PopotoVox.Casting;
using PopotoVox.Npc;
using PopotoVox.Tts;

namespace PopotoVox.Pipeline;

/// <summary>
/// Background pre-builder (M9b): when enabled, periodically scans nearby NPCs and pre-casts — and, on the
/// Ultra engine, pre-designs — their voices BEFORE the player talks to them, so first lines skip the
/// casting/design wait. OFF by default; range tiers bound how far it reaches so users match it to their
/// hardware.
///
/// Priority model (M17):
///   • <b>Distance-prioritized</b> — pending NPCs are kept in a map whose distances are refreshed every
///     scan, and the worker always builds the NEAREST un-built NPC first. As the player moves, a closer
///     NPC jumps ahead, and NPCs left behind (out of range) are pruned.
///   • <b>Foreground hard-halt</b> — a dialog box / attack line (foreground) is WAY more important, so the
///     worker halts (<see cref="YieldToLive"/>) while one is rendering or playing, and the in-flight
///     voice design is cancelled via the director's background token. Live casts also pause it.
///   • <b>Designs first</b> — a designed voice is what makes a first click fast, so all in-range designs
///     drain before the lower-priority ambient-line pre-render pass.
/// </summary>
public sealed class NpcPrecomputeService : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(3);
    private const int MaxAmbientLinesPerNpc = 3;

    private readonly IFramework framework;
    private readonly NpcResolver resolver;
    private readonly CastingDirector director;
    private readonly ActiveEngine active;
    private readonly CastingState state;
    private readonly Func<Configuration> config;
    private readonly NpcDialogueProbe dialogueProbe;
    private readonly IPluginLog log;

    // An NPC awaiting cast + design. Distance is refreshed each scan so the worker can reprioritize.
    private sealed class PendingDesign
    {
        public string Name = "";
        public float Distance;
        public int Attempts;
        public DateTime NextEligibleUtc;
    }

    // A cast+designed NPC awaiting (low-priority) ambient-line pre-render.
    private sealed class PendingAmbient
    {
        public VoiceSpec Spec = null!;
        public float Distance;
    }

    private readonly object gate = new();                               // guards the three maps + inFlight
    private readonly Dictionary<uint, PendingDesign> pendingDesigns = new();
    private readonly Dictionary<uint, PendingAmbient> pendingAmbient = new();
    private readonly HashSet<uint> built = new();                       // completed/given-up dedup ledger
    private uint inFlight;                                              // id the worker is processing (0 = none)

    private DateTime lastScan = DateTime.MinValue;
    private int pumping;                    // single-worker latch
    private int ambientFailures;            // consecutive ambient pre-render failures (circuit breaker)
    private volatile bool ambientDisabled;  // latches after repeated failures → stop ambient pre-render this session
    private volatile bool disposed;

    public NpcPrecomputeService(IFramework framework, NpcResolver resolver, CastingDirector director,
        ActiveEngine active, CastingState state, Func<Configuration> config,
        NpcDialogueProbe dialogueProbe, IPluginLog log)
    {
        this.framework = framework;
        this.resolver = resolver;
        this.director = director;
        this.active = active;
        this.state = state;
        this.config = config;
        this.dialogueProbe = dialogueProbe;
        this.log = log;
        framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework fw)
    {
        if (disposed) return;
        var range = config().NpcPrecompute;
        if (range == PrecomputeRange.Off)
        {
            ClearPending(); // turned off — stop the worker draining a stale backlog
            return;
        }

        var now = DateTime.UtcNow;
        if (now - lastScan < ScanInterval) return;
        lastScan = now;

        var (maxDist, cap) = range switch
        {
            PrecomputeRange.Near => (15f, 12),
            PrecomputeRange.Mid => (35f, 30),
            _ => (float.MaxValue, 80), // Zone
        };

        try
        {
            var nearby = resolver.NearbyNpcs(cap); // framework thread; nearest-first, fresh distances
            string? summary = null;
            lock (gate)
            {
                var inRange = new HashSet<uint>();
                foreach (var (baseId, name, dist) in nearby)
                {
                    if (dist > maxDist) continue;
                    inRange.Add(baseId);
                    if (built.Contains(baseId)) continue;

                    // Refresh the current distance of anything we already track (reprioritization), and add
                    // newly-seen NPCs. The in-flight NPC stays put — only skip re-INSERTING it as new.
                    if (pendingDesigns.TryGetValue(baseId, out var pd)) { pd.Distance = dist; pd.Name = name; }
                    else if (baseId != inFlight) pendingDesigns[baseId] = new PendingDesign { Name = name, Distance = dist };

                    if (pendingAmbient.TryGetValue(baseId, out var pa)) pa.Distance = dist;
                }

                // Prune NPCs the player walked away from (don't burn the GPU designing voices left behind).
                // Never prune the in-flight one or the `built` ledger.
                PruneOutOfRange(pendingDesigns, inRange);
                PruneOutOfRange(pendingAmbient, inRange);

                if (built.Count > 2000) built.Clear(); // bound memory across a long session

                if (pendingDesigns.Count > 0 || pendingAmbient.Count > 0) summary = DescribePendingLocked();
            }

            // Diagnostic (M17): a per-scan one-liner so the distance ORDER and the foreground state are
            // visible in the log — watch the "next:" name/distance change as you walk toward an NPC.
            if (summary != null)
                log.Debug($"[Precompute] scan {range}: {summary} | fg={director.IsForegroundActive}, liveCast={state.Any}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Precompute] scan failed.");
        }

        Pump();
    }

    private void PruneOutOfRange<T>(Dictionary<uint, T> map, HashSet<uint> inRange)
    {
        // caller holds `gate`
        List<uint>? remove = null;
        foreach (var id in map.Keys)
            if (id != inFlight && !inRange.Contains(id))
                (remove ??= new()).Add(id);
        if (remove != null)
            foreach (var id in remove) map.Remove(id);
    }

    private void ClearPending()
    {
        lock (gate) { pendingDesigns.Clear(); pendingAmbient.Clear(); }
    }

    /// <summary>One-line snapshot of the pending designs in nearest-first order (for the diagnostic log).
    /// Caller holds <see cref="gate"/>.</summary>
    private string DescribePendingLocked()
    {
        var top = new List<(string Name, float Dist)>();
        foreach (var kv in pendingDesigns) top.Add((kv.Value.Name, kv.Value.Distance));
        top.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var n = Math.Min(3, top.Count);
        var head = new List<string>(n);
        for (var i = 0; i < n; i++) head.Add($"{top[i].Name} @ {top[i].Dist:0.0}y");
        return $"{pendingDesigns.Count} design(s) pending → next: [{string.Join(", ", head)}]; " +
               $"{pendingAmbient.Count} ambient pending; {built.Count} built";
    }

    private void Pump()
    {
        if (Interlocked.Exchange(ref pumping, 1) == 1) return; // one worker at a time
        _ = Task.Run(async () =>
        {
            try
            {
                while (!disposed)
                {
                    if (config().NpcPrecompute == PrecomputeRange.Off) { ClearPending(); break; }
                    if (active.Swapping) break; // an engine swap is in progress — don't flood the (cold) new engine

                    // DESIGNS FIRST — and always the NEAREST eligible one, re-picked each loop so a closer
                    // NPC (from a fresh scan) jumps ahead of farther ones.
                    if (TakeNearestDesign(out var id, out var name, out var dist, out var remaining))
                    {
                        log.Debug($"[Precompute] → pick NEAREST design: {name} ({id}) @ {dist:0.0}y ({remaining} pending).");
                        await ProcessDesignAsync(id, name).ConfigureAwait(false);
                        await Task.Delay(150).ConfigureAwait(false);
                        continue;
                    }

                    if (!ambientDisabled && TakeNearestAmbient(out var ambId, out var ambSpec))
                    {
                        log.Debug($"[Precompute] → pick ambient pre-render for {ambId}.");
                        await PrerenderAmbientAsync(ambId, ambSpec).ConfigureAwait(false);
                        await Task.Delay(150).ConfigureAwait(false);
                        continue;
                    }

                    break; // nothing eligible in either map
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[Precompute] worker error.");
            }
            finally
            {
                Interlocked.Exchange(ref pumping, 0);
            }
        });
    }

    /// <summary>Pick the nearest eligible (past its backoff) pending design and mark it in-flight. The entry
    /// stays in the map until the work completes, so a concurrent scan refreshes its distance but doesn't
    /// re-add it.</summary>
    private bool TakeNearestDesign(out uint id, out string name, out float dist, out int remaining)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            PendingDesign? best = null;
            uint bestId = 0;
            foreach (var kv in pendingDesigns)
            {
                if (kv.Value.NextEligibleUtc > now) continue; // backing off after a transient miss
                if (best == null || kv.Value.Distance < best.Distance) { best = kv.Value; bestId = kv.Key; }
            }
            if (best == null) { id = 0; name = ""; dist = 0f; remaining = pendingDesigns.Count; return false; }
            inFlight = bestId;
            id = bestId;
            name = best.Name;
            dist = best.Distance;
            remaining = pendingDesigns.Count;
            return true;
        }
    }

    private bool TakeNearestAmbient(out uint id, out VoiceSpec spec)
    {
        lock (gate)
        {
            var hearing = config().PrerenderAmbientYalms;
            PendingAmbient? best = null;
            uint bestId = 0;
            List<uint>? remove = null;
            foreach (var kv in pendingAmbient)
            {
                if (kv.Value.Distance > hearing) { (remove ??= new()).Add(kv.Key); continue; } // left range
                if (best == null || kv.Value.Distance < best.Distance) { best = kv.Value; bestId = kv.Key; }
            }
            if (remove != null) foreach (var r in remove) pendingAmbient.Remove(r);
            if (best == null) { id = 0; spec = null!; return false; }
            pendingAmbient.Remove(bestId); // ambient pre-render is one-shot — take it out
            id = bestId;
            spec = best.Spec;
            return true;
        }
    }

    /// <summary>Cast then (Ultra only) design one NPC's voice. On success it's marked built; on a transient
    /// miss it backs off and stays pending; on a foreground preemption it's left to retry.</summary>
    private async Task ProcessDesignAsync(uint id, string name)
    {
        VoiceSpec? spec;
        try
        {
            await YieldToLive("cast").ConfigureAwait(false);
            spec = await director.PrecastAsync(id, name).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[Precompute] precast failed for {id}.");
            spec = null;
        }

        if (spec == null)
        {
            log.Debug($"[Precompute] cast skipped {name} ({id}) — live cast owns it / in flight; backing off.");
            Backoff(id); // a live cast owns the id, or a precast is already in flight — retry later
            return;
        }
        log.Debug($"[Precompute] cast done: {name} ({id}) [source={spec.Source}].");

        try
        {
            await YieldToLive("design").ConfigureAwait(false);
            if (!active.TryEnterRender(out var lease)) { Backoff(id); return; } // engine swap draining — retry later
            try { await lease.Binding.Engine.PredesignAsync(spec, director.BackgroundToken).ConfigureAwait(false); } // no-op on non-Ultra
            finally { lease.Exit(); }
        }
        catch (OperationCanceledException)
        {
            // A foreground line preempted the design — expected. Leave it pending to retry when foreground clears.
            log.Debug($"[Precompute] design PREEMPTED by foreground: {name} ({id}) — will retry.");
            lock (gate) { if (inFlight == id) inFlight = 0; }
            return;
        }
        catch (Exception ex)
        {
            if (disposed) return; // unloading — the host was killed under us; not a real failure
            log.Warning(ex, $"[Precompute] predesign failed for {id}.");
            lock (gate) { pendingDesigns.Remove(id); built.Add(id); if (inFlight == id) inFlight = 0; } // give up; live line still designs on demand
            return;
        }

        var cfg = config();
        bool ambientQueued;
        lock (gate)
        {
            var curDist = pendingDesigns.TryGetValue(id, out var pd) ? pd.Distance : float.MaxValue;
            pendingDesigns.Remove(id);
            built.Add(id);
            if (inFlight == id) inFlight = 0;

            // Defer ambient lines to the low-priority pass, gated by CURRENT distance (own hearing range).
            ambientQueued = cfg.PrerenderAmbientLines && !ambientDisabled && active.Current.Engine.IsReady && curDist <= cfg.PrerenderAmbientYalms;
            if (ambientQueued)
                pendingAmbient[id] = new PendingAmbient { Spec = spec, Distance = curDist };
        }
        log.Debug($"[Precompute] ✓ READY {name} ({id}) — cast+design cached" +
                       (ambientQueued ? " (ambient lines queued)." : "."));
    }

    private void Backoff(uint id)
    {
        lock (gate)
        {
            if (inFlight == id) inFlight = 0;
            if (!pendingDesigns.TryGetValue(id, out var pd)) return; // pruned meanwhile
            pd.Attempts++;
            if (pd.Attempts >= 5)
            {
                pendingDesigns.Remove(id);
                built.Add(id); // give up for this session (the 2000-clear eventually recycles it)
                return;
            }
            var backoffSecs = Math.Min(Math.Pow(2, pd.Attempts) * 3, 60);
            pd.NextEligibleUtc = DateTime.UtcNow.AddSeconds(backoffSecs);
        }
    }

    /// <summary>Render an NPC's resolvable, placeholder-free ambient lines into the line cache so their next
    /// bubble plays instantly. Bounded (cap per NPC), skips already-cached, and yields to live work.</summary>
    private async Task PrerenderAmbientAsync(uint npcId, VoiceSpec spec)
    {
        NpcDialoguePool pool;
        try { pool = dialogueProbe.Resolve(npcId); }
        catch (Exception ex) { log.Warning(ex, $"[Precompute] ambient resolve failed for {npcId}."); return; }

        var done = 0;
        foreach (var line in pool.Lines)
        {
            if (done >= MaxAmbientLinesPerNpc || ambientDisabled) break;
            if (line.HasMacro) continue;                 // placeholders (<name>) can't be pre-rendered correctly
            await YieldToLive("ambient").ConfigureAwait(false);
            try
            {
                // Goes through the director → same emotion direction (base mood + per-line) as a live line.
                if (await director.PrerenderLineAsync(spec, line.Text).ConfigureAwait(false)) done++;
                // NOTE (M15): do NOT reset the failure count on success — a host that crashes intermittently
                // would otherwise never trip the breaker. Failures accumulate and latch it off for the session.
            }
            catch (OperationCanceledException)
            {
                return; // M14: a foreground line preempted the engine — expected, not a fault; retry later
            }
            catch (EngineRenderException ex)
            {
                // Recoverable per-line miss (the voice host is still alive — e.g. a transient missing output
                // file). Skip just this line; do NOT count it toward the breaker. Previously these were
                // misclassified as host crashes and wrongly disabled ambient pre-render for the whole session.
                log.Debug($"[Precompute] ambient line skipped for {npcId} (recoverable: {ex.Message}).");
            }
            catch (Exception ex)
            {
                if (disposed) return; // the plugin is unloading — the host was killed under us; not a real crash
                log.Warning(ex, $"[Precompute] ambient render failed for {npcId}.");
                if (++ambientFailures >= 3) // M14 circuit breaker: only genuine host crashes (closed stdout / timeout) reach here
                {
                    ambientDisabled = true;
                    lock (gate) pendingAmbient.Clear(); // drop the backlog
                    log.Warning("[Precompute] ambient line pre-render paused for this session after repeated voice-host " +
                                "crashes (closed stdout / timeout). Designed voices still pre-build.");
                    return;
                }
            }
            await Task.Delay(200).ConfigureAwait(false); // pace
        }
        if (done > 0) log.Debug($"[Precompute] pre-rendered {done} ambient line(s) for {npcId}.");
    }

    /// <summary>Block while a foreground line (dialog box / attack line) is rendering or playing, or a live
    /// cast is in flight — background precompute is WAY lower priority and must never compete with it. Unlike
    /// the old gate this does NOT block on background ambient renders, so designs don't starve in busy areas.</summary>
    private async Task YieldToLive(string stage)
    {
        if (!(director.IsForegroundActive || state.Any)) return; // common case: nothing live, no log spam
        log.Debug($"[Precompute] ⏸ HALT before {stage} — foreground/live active (fg={director.IsForegroundActive}, liveCast={state.Any}).");
        var since = DateTime.UtcNow;
        while (!disposed && (director.IsForegroundActive || state.Any))
            await Task.Delay(400).ConfigureAwait(false);
        log.Debug($"[Precompute] ▶ RESUME ({stage}) after {(DateTime.UtcNow - since).TotalSeconds:0.0}s halted for foreground.");
    }

    public void Dispose()
    {
        disposed = true;
        framework.Update -= OnUpdate;
    }
}
