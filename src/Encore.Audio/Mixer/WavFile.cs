using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Encore.Audio;

/// <summary>
/// RIFF WAVE as the sound masters use it: 16-bit PCM at 48 kHz, one or two channels. Reading keeps the
/// samples as they are; <see cref="WavWriter"/> records the mixer's output the same way.
/// </summary>
public static class WavFile
{
    public const int SampleRate = 48_000;

    /// <summary>Reads a master; anything but 16-bit PCM at 48 kHz in one or two channels is refused.</summary>
    /// <exception cref="InvalidDataException">The file is not such a WAV.</exception>
    public static SoundClip Read(string path) => Read(path, File.ReadAllBytes(path));

    /// <summary>Reads a master's bytes, as <see cref="Read(string)"/> reads a file; <paramref name="name"/> names it in errors.</summary>
    /// <exception cref="InvalidDataException">The bytes are not such a WAV.</exception>
    public static SoundClip Read(string name, ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"{name}: not a RIFF WAVE file.");
        int? channels = null;
        short[]? samples = null;
        for (var at = 12; at + 8 <= data.Length;)
        {
            var id = data.Slice(at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 4, 4)), (uint)(data.Length - at - 8));
            var chunk = data.Slice(at + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16) throw new InvalidDataException($"{name}: short fmt chunk.");
                var format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                var count = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                var rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                // WAVE_FORMAT_EXTENSIBLE carries the PCM sub-format GUID's first two bytes.
                if (format == 0xFFFE && chunk.Length >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(chunk[24..]);
                if (format != 1 || bits != 16 || rate != SampleRate || count is < 1 or > 2)
                    throw new InvalidDataException($"{name}: {bits}-bit format {format} at {rate} Hz in {count} channel(s); the game plays 16-bit PCM at 48 kHz in one or two channels.");
                channels = count;
            }
            else if (id.SequenceEqual("data"u8))
            {
                samples = MemoryMarshal.Cast<byte, short>(chunk[..(chunk.Length & ~1)]).ToArray();
                if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(samples, samples);
            }
            at += 8 + size + (size & 1);
        }
        if (channels is not { } c || samples is null) throw new InvalidDataException($"{name}: no fmt or data chunk.");
        if (samples.Length % c != 0 || samples.Length == 0) throw new InvalidDataException($"{name}: the data is not whole frames.");
        return new SoundClip(samples, c);
    }
}

/// <summary>Writes interleaved float frames as a 16-bit PCM WAV, filling in the sizes when disposed.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly Stream _output;
    private readonly int _channels;
    private readonly byte[] _buffer = new byte[8192];
    private long _frames;
    private bool _disposed;

    public WavWriter(Stream output, int channels)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanSeek || !output.CanWrite) throw new ArgumentException("The stream must be writable and seekable.", nameof(output));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        _output = output;
        _channels = channels;
        WriteHeader(0);
    }

    /// <summary>Frames written so far.</summary>
    public long Frames => _frames;

    /// <summary>Appends whole frames, rounding to 16 bits and clipping at full scale.</summary>
    public void Append(ReadOnlySpan<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interleaved.Length % _channels != 0) throw new ArgumentException("The buffer must contain whole frames.", nameof(interleaved));
        while (!interleaved.IsEmpty)
        {
            var count = Math.Min(interleaved.Length, _buffer.Length / 2);
            for (var i = 0; i < count; i++)
            {
                var value = (short)Math.Clamp(MathF.Round(interleaved[i] * 32767f), -32768f, 32767f);
                BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(2 * i), value);
            }
            _output.Write(_buffer, 0, 2 * count);
            interleaved = interleaved[count..];
            _frames += count / _channels;
        }
    }

    private void WriteHeader(long frames)
    {
        var data = checked((uint)(frames * _channels * 2));
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], (ushort)_channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], WavFile.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], WavFile.SampleRate * _channels * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], (ushort)(_channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], data);
        _output.Write(header);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output.Position = 0;
        WriteHeader(_frames);
        _output.Flush();
    }
}
