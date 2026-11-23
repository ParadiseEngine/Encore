using System.Buffers.Binary;

namespace Encore.Audio.Music.Formats;

/// <summary>
/// Standard MIDI Files (formats 0, 1 and 2, whose tracks all play at once), voiced by the
/// <see cref="GeneralMidi"/> presets: each channel's program picks a melodic instrument, channel 10 plays the
/// drum kit, and every instrument gets the part (<see cref="ScorePart"/>) its family suggests, so the mixer
/// can arrange a MIDI song like any other.
/// </summary>
/// <remarks>
/// Read: notes (on, off, and on at velocity 0), program changes, the tempo map (or SMPTE time), and the
/// controllers volume (7), pan (10), expression (11), sustain pedal (64), reverb send (91), reset all
/// controllers (121) and all notes off (123, also 120). Volume, expression and velocity set a note's loudness
/// (each squared, as General MIDI's 40·log₁₀ curve has it) when it starts; pan and reverb send pick the
/// instrument variant the note plays. Pitch bend, aftertouch, banks and SysEx are skipped. The song lasts until
/// its last track ends (or its last note does, if later).
/// </remarks>
public sealed class MidiFormat : IMusicFormat
{
    private const int Channels = 16;
    private const int DrumChannel = 9;

    private MidiFormat()
    {
    }

    public static MidiFormat Instance { get; } = new();

    public string Name => "Standard MIDI File";

    public IReadOnlyList<string> Extensions { get; } = [".mid", ".midi"];

    public bool CanRead(ReadOnlySpan<byte> data) => data.Length >= 4 && data[..4].SequenceEqual("MThd"u8);

    public Song Read(ReadOnlySpan<byte> data)
    {
        var (division, events, endTick) = Parse(data);
        events.Sort(static (a, b) =>
            a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick)
            : a.Track != b.Track ? a.Track.CompareTo(b.Track)
            : a.Sequence.CompareTo(b.Sequence));
        var clock = new Clock(division);
        var builder = new Builder();
        foreach (var e in events)
        {
            var time = clock.Seconds(e.Tick);
            switch (e.Kind)
            {
                case EventKind.Tempo: clock.SetTempo(e.Tick, e.Value); break;
                case EventKind.NoteOn: builder.NoteOn(time, e.Channel, e.Data1, e.Data2); break;
                case EventKind.NoteOff: builder.NoteOff(time, e.Channel, e.Data1); break;
                case EventKind.Program: builder.Program(e.Channel, e.Data1); break;
                case EventKind.Controller: builder.Controller(time, e.Channel, e.Data1, e.Data2); break;
            }
        }
        var end = clock.Seconds(endTick);
        return builder.Finish(end);
    }

    private static (int Division, List<MidiEvent> Events, long EndTick) Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14 || !data[..4].SequenceEqual("MThd"u8))
            throw Json.Invalid("Not a Standard MIDI File (no MThd header).");
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (headerLength < 6 || headerLength > data.Length - 8)
            throw Json.Invalid("The MIDI header is truncated.");
        var format = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        int division = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
        if (format > 2)
            throw Json.Invalid($"Unsupported MIDI file format {format}.");
        if (division == 0 || ((division & 0x8000) != 0 && ((division & 0xFF) == 0 || (sbyte)(division >> 8) is not (-24 or -25 or -29 or -30))))
            throw Json.Invalid("The MIDI file's time division is invalid.");

        var events = new List<MidiEvent>();
        long endTick = 0;
        var position = 8 + (int)headerLength;
        var track = 0;
        while (position + 8 <= data.Length)
        {
            var id = data.Slice(position, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[(position + 4)..]);
            position += 8;
            if (length > data.Length - position)
                throw Json.Invalid($"MIDI chunk {track} runs past the end of the file.");
            var chunk = data.Slice(position, (int)length);
            position += (int)length;
            if (!id.SequenceEqual("MTrk"u8)) continue; // Unknown chunks are skipped, as the standard asks.
            endTick = Math.Max(endTick, ParseTrack(chunk, track, events));
            track++;
        }
        if (track == 0)
            throw Json.Invalid("The MIDI file has no tracks.");
        return (division, events, endTick);
    }

    /// <summary>Appends a track's events and returns the tick it ends at.</summary>
    private static long ParseTrack(ReadOnlySpan<byte> chunk, int track, List<MidiEvent> events)
    {
        var reader = new ByteReader(chunk);
        long tick = 0;
        byte status = 0;
        var sequence = 0;
        while (!reader.AtEnd)
        {
            tick += reader.VariableLength();
            var first = reader.Byte();
            if (first == 0xFF)
            {
                var type = reader.Byte();
                var payload = reader.Bytes(reader.VariableLength());
                status = 0;
                if (type == 0x2F) break;
                if (type == 0x51)
                {
                    if (payload.Length != 3) throw Json.Invalid($"MIDI track {track} has a malformed tempo.");
                    var microseconds = (payload[0] << 16) | (payload[1] << 8) | payload[2];
                    if (microseconds == 0) throw Json.Invalid($"MIDI track {track} sets a tempo of zero.");
                    events.Add(new MidiEvent(tick, track, sequence++, EventKind.Tempo, 0, 0, 0, microseconds));
                }
                continue;
            }
            if (first is 0xF0 or 0xF7)
            {
                reader.Bytes(reader.VariableLength());
                status = 0;
                continue;
            }
            if (first is > 0xF0)
            {
                // System common and real-time messages do not belong in files; step over them.
                reader.Bytes(first switch { 0xF1 or 0xF3 => 1, 0xF2 => 2, _ => 0 });
                continue;
            }

            byte data1;
            if (first < 0x80)
            {
                if (status == 0) throw Json.Invalid($"MIDI track {track} has data without a status byte.");
                data1 = first;
            }
            else
            {
                status = first;
                data1 = reader.Byte();
            }
            var channel = (byte)(status & 0x0F);
            switch (status & 0xF0)
            {
                case 0x80:
                    reader.Byte();
                    events.Add(new MidiEvent(tick, track, sequence++, EventKind.NoteOff, channel, Data(data1, track), 0, 0));
                    break;
                case 0x90:
                    var velocity = Data(reader.Byte(), track);
                    events.Add(new MidiEvent(tick, track, sequence++, velocity == 0 ? EventKind.NoteOff : EventKind.NoteOn, channel, Data(data1, track), velocity, 0));
                    break;
                case 0xB0:
                    events.Add(new MidiEvent(tick, track, sequence++, EventKind.Controller, channel, Data(data1, track), Data(reader.Byte(), track), 0));
                    break;
                case 0xC0:
                    events.Add(new MidiEvent(tick, track, sequence++, EventKind.Program, channel, Data(data1, track), 0, 0));
                    break;
                case 0xA0 or 0xE0:
                    reader.Byte(); // aftertouch, pitch bend: skipped
                    break;
                default: // 0xD0 channel pressure: one data byte, already read
                    break;
            }
        }
        return tick;
    }

    private static byte Data(byte value, int track) =>
        value < 0x80 ? value : throw Json.Invalid($"MIDI track {track} has a data byte above 127.");

    private enum EventKind : byte { Tempo, NoteOn, NoteOff, Program, Controller }

    private readonly record struct MidiEvent(long Tick, int Track, int Sequence, EventKind Kind, byte Channel, byte Data1, byte Data2, int Value);

    private ref struct ByteReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;

        public readonly bool AtEnd => position >= data.Length;

        public byte Byte() => position < data.Length ? data[position++] : throw Truncated();

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (count > data.Length - position) throw Truncated();
            var bytes = data.Slice(position, count);
            position += count;
            return bytes;
        }

        /// <summary>A variable-length quantity: seven bits a byte, high bit set on all but the last, at most four bytes.</summary>
        public int VariableLength()
        {
            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                var b = Byte();
                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }
            throw Json.Invalid("A MIDI variable-length number is longer than four bytes.");
        }

        private static InvalidDataException Truncated() => Json.Invalid("A MIDI track is truncated.");
    }

    /// <summary>Ticks to seconds through the tempo map (or SMPTE frames).</summary>
    private sealed class Clock
    {
        private readonly int division;
        private readonly bool smpte;
        private long tempoTick;
        private double tempoSeconds;
        private double secondsPerTick;

        public Clock(int division)
        {
            this.division = division;
            smpte = (division & 0x8000) != 0;
            secondsPerTick = smpte
                ? 1.0 / (-(sbyte)(division >> 8) * (division & 0xFF))
                : 0.5 / division; // 120 beats a minute until a tempo says otherwise
        }

        public double Seconds(long tick) => tempoSeconds + (tick - tempoTick) * secondsPerTick;

        public void SetTempo(long tick, int microsecondsPerBeat)
        {
            if (smpte) return; // SMPTE time ignores tempo
            tempoSeconds = Seconds(tick);
            tempoTick = tick;
            secondsPerTick = microsecondsPerBeat / 1e6 / division;
        }
    }

    /// <summary>Channel state, sounding notes, and the instruments made so far.</summary>
    private sealed class Builder
    {
        private readonly int[] program = new int[Channels];
        private readonly int[] volume = new int[Channels];
        private readonly int[] expression = new int[Channels];
        private readonly int[] pan = new int[Channels];
        private readonly int[] reverb = new int[Channels];
        private readonly bool[] pedal = new bool[Channels];
        private readonly List<Pending>?[] sounding = new List<Pending>?[Channels * 128];
        private readonly List<Pending>[] sustained = new List<Pending>[Channels];
        private readonly Dictionary<(int Program, int Pan, int Reverb), int> variants = [];
        private readonly List<Instrument> instruments = [];
        private readonly List<Note> notes = [];

        public Builder()
        {
            Array.Fill(volume, 100);
            Array.Fill(expression, 127);
            Array.Fill(pan, 64);
            Array.Fill(reverb, 40);
            for (var c = 0; c < Channels; c++) sustained[c] = [];
        }

        public void Program(int channel, int value) => program[channel] = value;

        public void NoteOn(double time, int channel, int key, int velocity)
        {
            // Drums are programs 128 + key in the variant table; melodic instruments are 0..127.
            var preset = channel == DrumChannel ? 128 + key : program[channel];
            var instrument = Variant(preset, pan[channel], reverb[channel]);
            double? pitch = channel == DrumChannel ? null : key;
            var loudness = Square(velocity) * Square(volume[channel]) * Square(expression[channel]);
            (sounding[channel * 128 + key] ??= new List<Pending>()).Add(new Pending(time, pitch, (float)loudness, instrument));
        }

        public void NoteOff(double time, int channel, int key)
        {
            var list = sounding[channel * 128 + key];
            if (list is null || list.Count == 0) return;
            var note = list[0];
            list.RemoveAt(0);
            if (pedal[channel]) sustained[channel].Add(note);
            else End(note, time);
        }

        public void Controller(double time, int channel, int number, int value)
        {
            switch (number)
            {
                case 7: volume[channel] = value; break;
                case 10: pan[channel] = value; break;
                case 11: expression[channel] = value; break;
                case 91: reverb[channel] = value; break;
                case 64:
                    pedal[channel] = value >= 64;
                    if (!pedal[channel]) ReleasePedal(channel, time);
                    break;
                case 121:
                    expression[channel] = 127;
                    pedal[channel] = false;
                    ReleasePedal(channel, time);
                    break;
                case 120 or 123:
                    for (var key = 0; key < 128; key++)
                    {
                        var list = sounding[channel * 128 + key];
                        if (list is null) continue;
                        foreach (var note in list) End(note, time);
                        list.Clear();
                    }
                    ReleasePedal(channel, time);
                    break;
            }
        }

        public Song Finish(double trackEnd)
        {
            // Notes still held when the tracks end stop there (or where they started, if later).
            var end = trackEnd;
            for (var c = 0; c < Channels; c++) ReleasePedal(c, end);
            foreach (var list in sounding)
            {
                if (list is null) continue;
                foreach (var note in list) End(note, end);
                list.Clear();
            }
            if (notes.Count == 0)
                throw Json.Invalid("The MIDI file has no notes.");
            foreach (var note in notes) end = Math.Max(end, note.Start + note.Duration);
            // A note may not start at the very end; leave it a moment to sound.
            foreach (var note in notes)
                if (note.Start >= end) end = note.Start + 0.001;
            try
            {
                return new Song(instruments, notes, end, [GeneralMidi.Room]);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(exception.Message, exception);
            }
        }

        private void ReleasePedal(int channel, double time)
        {
            foreach (var note in sustained[channel]) End(note, time);
            sustained[channel].Clear();
        }

        private void End(Pending note, double time) =>
            notes.Add(new Note(note.Start, Math.Max(0, time - note.Start), note.Pitch, note.Velocity, note.Instrument));

        private int Variant(int preset, int panValue, int reverbValue)
        {
            if (variants.TryGetValue((preset, panValue, reverbValue), out var index)) return index;
            var instrument = preset >= 128 ? GeneralMidi.Drum(preset - 128) : GeneralMidi.Melodic(preset);
            var panning = Math.Clamp((panValue - 64) / 63.0, -1, 1);
            // The reverb send scales the preset's own (CC 91 defaults to 40), so a dry preset stays dry.
            var sends = instrument.Sends ?? new Sends(0);
            instrument = instrument with
            {
                Pan = Math.Clamp(instrument.Pan + panning, -1, 1),
                Sends = sends with { Bus = 0, Reverb = sends.Reverb * reverbValue / 40.0 },
            };
            index = instruments.Count;
            instruments.Add(instrument);
            variants.Add((preset, panValue, reverbValue), index);
            return index;
        }

        private static double Square(int value) => value / 127.0 * (value / 127.0);

        private readonly record struct Pending(double Start, double? Pitch, float Velocity, int Instrument);
    }
}
