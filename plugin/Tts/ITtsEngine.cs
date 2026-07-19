using System;
using System.Threading;
using System.Threading.Tasks;

namespace PopotoVox.Tts;

/// <summary>
/// Receives decoded PCM as it is produced, so playback can begin before a whole line is finished
/// (streaming engines). <see cref="Begin"/> is called once with the format, then <see cref="Feed"/> is
/// called for each chunk in order.
/// </summary>
public interface IAudioSink
{
    void Begin(int sampleRate, int channels);
    void Feed(byte[] pcm16);
}

/// <summary>
/// Renders text to audio for a given locked voice. Kept deliberately small and
/// engine-agnostic (PRD D5: engine/model interface pluggable) — nothing above this
/// interface knows which concrete engine is running.
/// </summary>
public interface ITtsEngine : IDisposable
{
    /// <summary>True once the engine's binary and model are present and usable.</summary>
    bool IsReady { get; }

    Task<RenderedAudio> RenderAsync(string text, VoiceSpec spec, RenderContext? ctx = null, CancellationToken ct = default);

    /// <summary>
    /// Optional: pre-load the engine (e.g. spin up a slow GPU model) so the first real
    /// line doesn't stall. Default is a no-op; the GPU engine overrides it.
    /// </summary>
    Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Optional: pre-build (and cache) the heavy per-voice assets for <paramref name="spec"/> ahead of
    /// time, so the first line for that voice skips the cold start. Default no-op; the Ultra engine
    /// overrides it to design the voice in advance (used by background NPC precompute).
    /// </summary>
    Task PredesignAsync(VoiceSpec spec, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Optional: render straight into <paramref name="sink"/> as audio is produced, so the first sound plays
    /// seconds before the line finishes. Only called for engines whose catalog entry has
    /// <c>SupportsStreaming</c>; the default throws. The Ultra engine (VoxCPM2) overrides it.
    /// </summary>
    Task RenderStreamingAsync(string text, VoiceSpec spec, IAudioSink sink, RenderContext? ctx = null, CancellationToken ct = default) =>
        throw new NotSupportedException("This engine does not support streaming.");
}
