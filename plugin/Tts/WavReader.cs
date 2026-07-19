using System;
using System.Buffers.Binary;
using System.IO;

namespace PopotoVox.Tts;

/// <summary>
/// Reads exactly one canonical RIFF/WAVE stream from a sequential stream. We run
/// Piper with WAV-to-stdout so each synthesized line is a self-delimiting WAV
/// (header carries the data length) — that's how we frame utterances out of one
/// long-lived Piper process without an inter-utterance marker.
/// </summary>
internal static class WavReader
{
    // A single utterance is at most a few seconds of 22 kHz 16-bit mono PCM; this
    // cap guards against a desynced/garbage stream demanding a huge allocation.
    private const int MaxChunkBytes = 64 * 1024 * 1024;

    public static RenderedAudio ReadOne(Stream stream)
    {
        // RIFF header: "RIFF" <size:4> "WAVE"
        Span<byte> riff = stackalloc byte[12];
        stream.ReadExactly(riff);
        if (riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F' ||
            riff[8] != 'W' || riff[9] != 'A' || riff[10] != 'V' || riff[11] != 'E')
            throw new InvalidDataException("Piper output is not a RIFF/WAVE stream.");

        int sampleRate = 22050, channels = 1, bitsPerSample = 16;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> pad = stackalloc byte[1];
        while (true)
        {
            stream.ReadExactly(chunkHeader);
            var id = chunkHeader[..4];
            var sizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]);
            if (sizeRaw > MaxChunkBytes)
                throw new InvalidDataException($"WAV chunk size {sizeRaw} exceeds the {MaxChunkBytes}-byte limit.");
            var size = (int)sizeRaw;

            if (id[0] == 'f' && id[1] == 'm' && id[2] == 't' && id[3] == ' ')
            {
                if (size < 16)
                    throw new InvalidDataException($"WAV fmt chunk too small ({size} bytes).");
                var fmt = new byte[size];
                stream.ReadExactly(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (size % 2 == 1) stream.ReadExactly(pad); // chunk padding
            }
            else if (id[0] == 'd' && id[1] == 'a' && id[2] == 't' && id[3] == 'a')
            {
                var data = new byte[size];
                stream.ReadExactly(data);
                if (bitsPerSample != 16)
                    throw new InvalidDataException($"Unexpected bit depth {bitsPerSample} (expected 16).");
                return new RenderedAudio(data, sampleRate, channels);
            }
            else
            {
                // Unknown chunk — skip its (padded) length.
                Skip(stream, size + (size % 2));
            }
        }
    }

    private static void Skip(Stream stream, int count)
    {
        Span<byte> scratch = stackalloc byte[1024];
        while (count > 0)
        {
            var n = stream.Read(scratch[..Math.Min(scratch.Length, count)]);
            if (n <= 0) throw new EndOfStreamException();
            count -= n;
        }
    }
}
