using System;

namespace PopotoVox.Tts;

/// <summary>
/// A single line failed to render but the engine/host is still <b>alive and usable</b> — e.g. the Python
/// host reported a per-line error, or its output WAV was missing (a transient/desync hiccup). This is
/// deliberately distinct from a <b>host death</b> (closed stdout / render timeout), which surfaces as
/// <see cref="InvalidOperationException"/> / <see cref="TimeoutException"/> and justifies a respawn and the
/// ambient circuit breaker.
///
/// Callers should skip just this one line and carry on — they must NOT count it toward the session-long
/// ambient breaker, because the host isn't broken.
/// </summary>
public sealed class EngineRenderException : Exception
{
    public EngineRenderException(string message) : base(message) { }
    public EngineRenderException(string message, Exception inner) : base(message, inner) { }
}
