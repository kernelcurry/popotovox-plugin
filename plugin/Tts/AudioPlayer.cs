using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PopotoVox.Tts;

/// <summary>Opaque per-voice handle into the mixer. <see cref="None"/> means "not playing" (e.g. the
/// concurrency cap rejected the line, or the source was empty).</summary>
public readonly record struct VoiceHandle(long Id)
{
    public static readonly VoiceHandle None = new(0);
    public bool IsValid => Id != 0;
}

/// <summary>
/// Multi-channel playback: several voice lines can play AT ONCE through a single NAudio mixer (overlapping
/// ambient bubbles when their balloons pop, each at its own distance volume, each spatially tracked live).
/// One persistent <see cref="WaveOutEvent"/> feeds a <see cref="MixingSampleProvider"/> that runs forever
/// (ReadFully → silence when idle); each line is a mixer input added on play and removed when it ends. A
/// per-voice <see cref="VoiceHandle"/> lets the director/spatial tracker set distance + priority per line.
///
/// Each input owns its disposables and is torn down exactly once (<see cref="VoiceInput.DisposeOnce"/>'s
/// Interlocked guard), whether removal is driven by the mixer's MixerInputEnded callback (one-shots that
/// played out) or by us (streams, eviction, Stop, Dispose) — so the callback thread and the game thread can
/// never double-free.
///
/// ⚠️ VOLUME IS ALWAYS APPLIED AT THE SAMPLE LEVEL (<see cref="VolumeSampleProvider"/>) — NEVER via
/// <c>WaveOutEvent.Volume</c> / <c>waveOutSetVolume</c>, and NEVER via the Core Audio session
/// (<c>SimpleAudioVolume</c>). This plugin runs IN-PROCESS with the game, so all of those change FFXIV's
/// WHOLE Windows audio session — and Windows PERSISTS that per-app level in the registry
/// (HKCU\…\Audio\PolicyConfig\PropertyStore), so a bad value survives reboots and can only be cleared by
/// editing the registry (this actually happened — it muted the user's game for a whole session). Scaling our
/// own PCM samples can only ever affect our own audio; the device/session volume must stay untouched (1.0).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    // Fixed mixer format: mono float at 48 kHz. MixingSampleProvider is float-only and requires every input
    // to share one format; per-input resampling (WdlResamplingSampleProvider, pure-managed — no MediaFoundation
    // COM on the playback thread) brings each engine's PCM (rates vary: 22050 / 24000 / …) up to this. Mono
    // because v1 attenuates by distance only (no left/right panning).
    private const int MixRate = 48000;
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(MixRate, 1);

    // Mirrors CastingDirector.ForegroundThreshold: a voice at or above this is "foreground" (dialogue/combat).
    // While any foreground voice is live, background voices duck to a fraction of their distance volume so the
    // conversation stays clearly on top without going silent (the hub stays alive).
    private const int ForegroundThreshold = 2;
    private const float DuckFactor = 0.25f; // background plays at 25% of its current distance volume under foreground

    private readonly object addGate = new(); // serializes the compound cap-check + add/evict; NOT held in the callback
    private readonly WaveOutEvent device;
    private readonly MixingSampleProvider mixer;
    private readonly ConcurrentDictionary<long, VoiceInput> inputs = new();
    private readonly Func<Configuration>? config;
    private long nextId;
    private float volume = 1.0f;
    private volatile bool foregroundActive; // any input Priority >= ForegroundThreshold → duck the rest

    public AudioPlayer(Func<Configuration>? config = null)
    {
        this.config = config;
        mixer = new MixingSampleProvider(MixFormat) { ReadFully = true }; // never ends → device stays alive
        mixer.MixerInputEnded += OnMixerInputEnded;
        device = new WaveOutEvent(); // device/session volume STAYS 1.0 — all gain is sample-level (HARD RULE)
        device.Init(mixer);
        device.Play();
    }

    /// <summary>Simultaneous playback cap — "scaled to the machine". 0/unset → clamp(cores, 4, 16).</summary>
    private int MaxVoices
    {
        get
        {
            var n = config?.Invoke().MaxConcurrentVoices ?? 0;
            return n > 0 ? Math.Clamp(n, 1, 64) : Math.Clamp(Environment.ProcessorCount, 4, 16);
        }
    }

    public float Volume
    {
        get => volume;
        set { volume = Math.Clamp(value, 0f, 1f); foreach (var v in inputs.Values) v.ApplyGain(volume, foregroundActive); }
    }

    /// <summary>Monotonic count of streaming underruns across all voices (the choppy-audio diagnostic).</summary>
    public int StreamUnderrunCount { get; private set; }

    /// <summary>True while any voice is playing.</summary>
    public bool IsPlaying => !inputs.IsEmpty;

    /// <summary>True if any voice of at least <paramref name="priority"/> is playing (foreground gating).</summary>
    public bool IsPlayingAtLeast(int priority)
    {
        foreach (var v in inputs.Values) if (v.Priority >= priority) return true;
        return false;
    }

    /// <summary>True while this specific voice is still a live mixer input.</summary>
    public bool IsLive(VoiceHandle handle) => handle.IsValid && inputs.ContainsKey(handle.Id);

    /// <summary>Play a finished clip. Returns a handle, or <see cref="VoiceHandle.None"/> if the cap rejected it.</summary>
    public VoiceHandle Play(RenderedAudio audio, float volumeScale = 1f, int priority = int.MaxValue)
    {
        if (audio?.Pcm16 is not { Length: > 0 }) return VoiceHandle.None;
        var ms = new MemoryStream(audio.Pcm16, writable: false);
        var source = new RawSourceWaveStream(ms, new WaveFormat(audio.SampleRate, 16, audio.Channels));
        var vi = BuildInput(source.ToSampleProvider(), audio.Channels, volumeScale, priority, isStream: false);
        vi.OneShotSource = source;
        vi.OneShotMs = ms;
        return Register(vi);
    }

    /// <summary>Begin a streamed line (PCM fed in chunks via <see cref="Feed"/>). Returns its handle.</summary>
    public VoiceHandle Begin(int sampleRate, int channels) => Begin(sampleRate, channels, 1f, int.MaxValue);

    public VoiceHandle Begin(int sampleRate, int channels, float volumeScale, int priority = int.MaxValue)
    {
        var buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            BufferDuration = TimeSpan.FromSeconds(60),
            DiscardOnBufferOverflow = true,
            ReadFully = true, // underrun → brief silence, NOT end-of-stream; removed explicitly in EndStream
        };
        var vi = BuildInput(buffer.ToSampleProvider(), channels, volumeScale, priority, isStream: true);
        vi.StreamBuffer = buffer;
        return Register(vi);
    }

    /// <summary>Append a PCM chunk to a streamed voice. Audio flows immediately (the device is always running).</summary>
    public void Feed(VoiceHandle handle, byte[] pcm16)
    {
        if (!inputs.TryGetValue(handle.Id, out var vi) || vi.StreamBuffer is not { } buf) return;
        // If playback already drained everything before this chunk arrived, that was an underrun gap
        // (a brief silence the listener heard) — generation isn't keeping real-time pace.
        if (vi.Started && buf.BufferedBytes == 0) StreamUnderrunCount++;
        try { buf.AddSamples(pcm16, 0, pcm16.Length); } catch { /* buffer full/torn down — drop */ }
        vi.Started = true;
    }

    /// <summary>No more chunks for this streamed voice: once everything fed has played out (ReadFully keeps it
    /// alive until then), remove it from the mix.</summary>
    public void EndStream(VoiceHandle handle)
    {
        if (!inputs.TryGetValue(handle.Id, out var started) || !started.IsStream) return;
        Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 600; i++) // ~60s safety cap
                {
                    if (!inputs.TryGetValue(handle.Id, out var cur) || cur.StreamBuffer is not { } b) return; // gone
                    if (b.BufferedBytes <= 0) break;
                    await Task.Delay(100).ConfigureAwait(false);
                }
                await Task.Delay(200).ConfigureAwait(false); // flush the device tail
                Remove(handle.Id);
            }
            catch { /* ignore */ }
        });
    }

    /// <summary>Live-update one voice's distance attenuation as the player moves past its speaker (spatial tracking).</summary>
    public void SetDistanceScale(VoiceHandle handle, float scale)
    {
        if (inputs.TryGetValue(handle.Id, out var vi))
        {
            vi.DistanceScale = Math.Clamp(scale, 0f, 1f);
            vi.ApplyGain(volume, foregroundActive);
        }
    }

    /// <summary>Immediately stop and remove one voice (e.g. its speaker despawned mid-line).</summary>
    public void Stop(VoiceHandle handle) => Remove(handle.Id);

    /// <summary>Stop everything (clears the whole mix).</summary>
    public void Stop()
    {
        foreach (var id in inputs.Keys) Remove(id);
    }

    private VoiceInput BuildInput(ISampleProvider sample, int channels, float volumeScale, int priority, bool isStream)
    {
        if (channels == 2) sample = new StereoToMonoSampleProvider(sample); // mixer is mono
        var gain = new VolumeSampleProvider(sample); // sample-level gain — NEVER WaveOutEvent.Volume/session volume
        ISampleProvider final = gain.WaveFormat.SampleRate == MixRate
            ? gain
            : new WdlResamplingSampleProvider(gain, MixRate);
        return new VoiceInput
        {
            Gain = gain,
            Final = final,
            Priority = priority,
            DistanceScale = Math.Clamp(volumeScale, 0f, 1f),
            IsStream = isStream,
        };
    }

    private VoiceHandle Register(VoiceInput vi)
    {
        lock (addGate)
        {
            // Enforce the playback cap: if full, evict the lowest-priority voice that's strictly below this one;
            // if there's nothing lower, drop the new line rather than cut something more important.
            if (inputs.Count >= MaxVoices && !EvictForLocked(vi.Priority))
            {
                vi.DisposeOnce();
                return VoiceHandle.None;
            }
            vi.Id = Interlocked.Increment(ref nextId);
            vi.ApplyGain(volume, foregroundActive || vi.Priority >= ForegroundThreshold);
            inputs[vi.Id] = vi;
            mixer.AddMixerInput(vi.Final); // takes the mixer's internal sources lock — never held under addGate elsewhere except here
        }
        RecomputeDuck(); // a new foreground voice ducks the rest (or a new background voice may need ducking itself)
        return new VoiceHandle(vi.Id);
    }

    // Caller holds addGate.
    private bool EvictForLocked(int newPriority)
    {
        VoiceInput? victim = null;
        foreach (var v in inputs.Values)
            if (v.Priority < newPriority && (victim is null || v.Priority < victim.Priority))
                victim = v;
        if (victim is null) return false;
        inputs.TryRemove(victim.Id, out _);
        try { mixer.RemoveMixerInput(victim.Final); } catch { /* already gone */ }
        victim.DisposeOnce();
        return true;
    }

    private void Remove(long id)
    {
        if (inputs.TryRemove(id, out var vi))
        {
            try { mixer.RemoveMixerInput(vi.Final); } catch { /* already gone */ }
            vi.DisposeOnce();
            RecomputeDuck();
        }
    }

    // The mixer removed a played-out one-shot from its sources and told us. Runs on the WaveOut thread INSIDE
    // the mixer's sources lock, so it MUST NOT take addGate (Register/Evict hold addGate while calling
    // AddMixerInput/RemoveMixerInput, which take that same sources lock → would deadlock). ConcurrentDictionary
    // removal is lock-free, so this is safe.
    private void OnMixerInputEnded(object? sender, SampleProviderEventArgs e)
    {
        long id = 0;
        foreach (var kv in inputs)
            if (ReferenceEquals(kv.Value.Final, e.SampleProvider)) { id = kv.Key; break; }
        if (id != 0 && inputs.TryRemove(id, out var vi))
        {
            vi.DisposeOnce();
            RecomputeDuck();
        }
    }

    // Recompute foreground-active state and re-apply ducking to all inputs. Called after every add/remove;
    // racy by nature (cosmetic gain), so it just converges to the right state.
    private void RecomputeDuck()
    {
        var fg = false;
        foreach (var v in inputs.Values) if (v.Priority >= ForegroundThreshold) { fg = true; break; }
        foregroundActive = fg;
        foreach (var v in inputs.Values) v.ApplyGain(volume, fg);
    }

    public void Dispose()
    {
        try { mixer.MixerInputEnded -= OnMixerInputEnded; } catch { /* ignore */ }
        try { device.Stop(); } catch { /* ignore */ }
        foreach (var v in inputs.Values) v.DisposeOnce();
        inputs.Clear();
        try { device.Dispose(); } catch { /* ignore */ }
    }

    private sealed class VoiceInput
    {
        public long Id;
        public required ISampleProvider Final { get; init; }      // what's added to the mixer
        public required VolumeSampleProvider Gain { get; init; }  // sample-level volume (device/session left untouched)
        public BufferedWaveProvider? StreamBuffer;                // non-null for streamed voices
        public RawSourceWaveStream? OneShotSource;                // non-null for one-shot voices
        public MemoryStream? OneShotMs;
        public int Priority;
        public float DistanceScale = 1f;
        public bool Started;   // streams: first chunk fed (underrun detection)
        public bool IsStream;
        private int disposed;

        public void ApplyGain(float master, bool foregroundActive)
        {
            var duck = (foregroundActive && Priority < ForegroundThreshold) ? DuckFactor : 1f;
            Gain.Volume = Math.Clamp(master * DistanceScale * duck, 0f, 1f);
        }

        public void DisposeOnce()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { OneShotSource?.Dispose(); } catch { /* ignore */ }
            try { OneShotMs?.Dispose(); } catch { /* ignore */ }
            // BufferedWaveProvider holds only managed buffers — nothing to dispose.
        }
    }
}
