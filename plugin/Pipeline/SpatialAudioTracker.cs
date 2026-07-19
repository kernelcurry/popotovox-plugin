using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using PopotoVox.Tts;

namespace PopotoVox.Pipeline;

/// <summary>
/// M16 / mixer: follow each playing AMBIENT voice's speaker live and update THAT voice's volume in real time —
/// so a bubble swells as you approach and fades as you walk past, independently per overlapping voice. The
/// director registers a voice handle → speaker address when an ambient line starts and the tracker drives the
/// per-voice distance scale every tick (releasing voices that ended or whose speaker despawned).
///
/// It also publishes a lock-free <b>presence snapshot</b> of live object addresses each tick, which the
/// director reads OFF the framework thread to decide, at play time, whether a bubble's speaker is still there
/// (the "only start if the bubble is still up" rule). We must never enumerate the object table off the
/// framework thread (it can crash the game), so the snapshot is the safe cross-thread bridge.
/// </summary>
public sealed class SpatialAudioTracker : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50); // ~20 Hz — smooth volume ramps

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly AudioPlayer audio;
    private readonly Func<Configuration> config;

    private readonly ConcurrentDictionary<long, nint> tracked = new(); // voice handle id → live object address
    private volatile HashSet<nint> presence = new();                   // addresses present as of the last tick
    private DateTime lastTick = DateTime.MinValue;                     // framework-thread only

    public SpatialAudioTracker(IFramework framework, IObjectTable objects, AudioPlayer audio, Func<Configuration> config)
    {
        this.framework = framework;
        this.objects = objects;
        this.audio = audio;
        this.config = config;
        framework.Update += OnUpdate;
    }

    /// <summary>Follow this speaker for the given playing voice (ambient). Address 0 → no-op.</summary>
    public void Track(VoiceHandle handle, nint speakerAddress)
    {
        if (handle.IsValid && speakerAddress != 0) tracked[handle.Id] = speakerAddress;
    }

    /// <summary>Stop following a voice (it ended, or it's interactive dialogue with a fixed volume).</summary>
    public void Untrack(VoiceHandle handle) => tracked.TryRemove(handle.Id, out _);

    /// <summary>Was this object present as of the last tick? Safe to call off the framework thread (reads the
    /// published snapshot). ≤ one tick (~50 ms) stale, which is fine for the play-time "is the bubble still up" gate.</summary>
    public bool IsPresent(nint address) => address != 0 && presence.Contains(address);

    private void OnUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if (now - lastTick < Tick) return;
        lastTick = now;

        var cfg = config();
        // Only scan the table when something needs it: voices to track, or ambient capture on (so the presence
        // snapshot is ready for the liveness gate). Otherwise drop the (now stale) snapshot and skip the work.
        if (tracked.IsEmpty && !cfg.CaptureMiniTalk)
        {
            if (presence.Count != 0) presence = new HashSet<nint>();
            return;
        }

        // One framework-thread pass: build the presence snapshot AND collect positions for tracked speakers.
        var snapshot = new HashSet<nint>();
        Dictionary<nint, Vector3>? positions = tracked.IsEmpty ? null : new Dictionary<nint, Vector3>();
        foreach (var obj in objects)
        {
            var addr = obj.Address;
            if (addr == 0) continue;
            snapshot.Add(addr);
            if (positions != null) positions[addr] = obj.Position;
        }
        presence = snapshot; // publish (volatile swap — readers see the whole set atomically)

        if (tracked.IsEmpty) return;
        if (objects[0]?.Position is not { } origin) return; // local player

        var live = cfg.AmbientSpatialTracking; // live volume ramps; when off, leave each voice's initial volume
        foreach (var (id, addr) in tracked)
        {
            var handle = new VoiceHandle(id);
            if (!audio.IsLive(handle)) { tracked.TryRemove(id, out _); continue; } // voice finished — stop tracking
            if (positions != null && positions.TryGetValue(addr, out var sp))
            {
                if (live) audio.SetDistanceScale(handle, AmbientVolume.Scale(Vector3.Distance(origin, sp), cfg.AmbientHearingYalms));
            }
            else
            {
                audio.SetDistanceScale(handle, 0f); // speaker despawned → fade this voice out
                tracked.TryRemove(id, out _);
            }
        }
    }

    public void Dispose() => framework.Update -= OnUpdate;
}
