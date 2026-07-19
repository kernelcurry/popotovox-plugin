namespace PopotoVox.Tts;

/// <summary>
/// A finished synthesis result: raw little-endian 16-bit PCM plus the format
/// needed to play it back. libritts-high renders mono at 22050 Hz.
/// </summary>
public sealed record RenderedAudio(byte[] Pcm16, int SampleRate, int Channels);
