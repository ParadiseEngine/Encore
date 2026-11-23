using Encore.Audio.Music;
using Encore.Audio.Music.Formats;
using Encore.Audio.Music.Synth;

namespace Encore.Audio;

/// <summary>
/// Plays a <see cref="Song"/> at 48 kHz stereo on the built-in synthesizer, each of its parts
/// (<see cref="ScorePart"/>) at the gain its arrangement gives it. Songs are built in code or read by
/// <see cref="MusicFormats"/> (this engine's <c>encore.song</c> JSON, Standard MIDI Files). The caller must
/// serialize all access.
/// </summary>
/// <remarks>
/// The synthesizer renders whole blocks of <see cref="Sequencer.Block"/> frames and the player hands them out
/// in whatever slices the caller asks for, so the samples never depend on the caller's buffer sizes. Rendering
/// allocates nothing. Repeating playback starts every note again each <see cref="Length"/>, voices and effect
/// tails carrying over; a single pass ends at <see cref="Length"/>, cutting the tails.
/// </remarks>
public sealed class MusicPlayer
{
    public const int SampleRate = Dsp.SampleRate;
    public const int Channels = 2;

    private readonly Sequencer synth;
    private readonly float[] blockLeft = new float[Sequencer.Block];
    private readonly float[] blockRight = new float[Sequencer.Block];
    private int served = Sequencer.Block; // frames of the current block already handed out
    private long played;                  // frames handed out since Play
    private bool repeat;
    private float gain = 1f;

    /// <exception cref="ArgumentException">The song lasts less than one output frame.</exception>
    public MusicPlayer(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        LengthFrames = (long)(song.Length * SampleRate);
        if (LengthFrames < 1)
            throw new ArgumentException("The song must last at least one output frame.", nameof(song));
        Song = song;
        Length = TimeSpan.FromTicks((long)Math.Round(song.Length * TimeSpan.TicksPerSecond));
        synth = new Sequencer(song, LengthFrames);
    }

    /// <summary>Loads a song file in any format <see cref="MusicFormats"/> reads. File exceptions propagate to the caller.</summary>
    /// <exception cref="ArgumentException">The path is null, empty or whitespace.</exception>
    /// <exception cref="InvalidDataException">The file is in no format the player reads, is malformed, or lasts less than a frame.</exception>
    public MusicPlayer(string path) : this(Audible(MusicFormats.Load(path), path))
    {
    }

    /// <summary>Loads a readable stream from its current position to its end, leaving it open.</summary>
    /// <exception cref="ArgumentException">The stream is null or unreadable.</exception>
    /// <exception cref="InvalidDataException">The data is in no format the player reads, is malformed, or lasts less than a frame.</exception>
    public MusicPlayer(Stream stream) : this(Audible(MusicFormats.Load(stream), "The song"))
    {
    }

    public Song Song { get; }

    /// <summary>The song's length: one pass, or the period of repetition.</summary>
    public TimeSpan Length { get; }

    /// <summary>Frames in one pass: floor(length × 48000).</summary>
    public long LengthFrames { get; }

    /// <summary>Where playback is within the current pass, to the frame.</summary>
    public TimeSpan Position { get; private set; }

    public MusicPlaybackState State { get; private set; }

    /// <summary>The voices sounding now.</summary>
    internal int ActiveVoices => synth.ActiveVoices;

    /// <summary>Output gain in [0, 1], initially 1; takes effect from the next render.</summary>
    public float Volume
    {
        get => gain;
        set => gain = float.IsFinite(value) && value is >= 0f and <= 1f
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Volume must be finite and between 0 and 1.");
    }

    /// <summary>
    /// Moves each part's gain (one per <see cref="ScorePart"/>, 0 to 1) in a straight line to <paramref name="gains"/>
    /// over <paramref name="seconds"/> (0: at once). The arrangement lasts through <see cref="Play"/> and
    /// <see cref="Stop"/>. A part's gain applies before its effect sends, so fading a part out leaves only the
    /// echoes it already sent.
    /// </summary>
    /// <exception cref="ArgumentException">Not one gain per part, a gain outside 0 to 1, or a negative or non-finite time.</exception>
    public void Arrange(ReadOnlySpan<float> gains, double seconds)
    {
        if (gains.Length != ScoreParts.Count)
            throw new ArgumentException($"An arrangement has one gain per part ({ScoreParts.Count}).", nameof(gains));
        foreach (var value in gains)
            if (!float.IsFinite(value) || value is < 0f or > 1f)
                throw new ArgumentException("A part's gain must be finite and between 0 and 1.", nameof(gains));
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentException("An arrangement's time must be finite and not negative.", nameof(seconds));
        synth.Arrange(gains, seconds);
    }

    /// <summary>A part's arranged gain at this moment.</summary>
    public float PartGain(ScorePart part) => synth.PartGain(part);

    /// <summary>Plays from the start, whatever the state; <paramref name="loop"/> repeats the song endlessly.</summary>
    public void Play(bool loop = true)
    {
        repeat = loop;
        Rewind();
        State = MusicPlaybackState.Playing;
    }

    /// <summary>Holds playback where it is; only while playing.</summary>
    public void Pause()
    {
        if (State is MusicPlaybackState.Playing) State = MusicPlaybackState.Paused;
    }

    /// <summary>Goes on from where <see cref="Pause"/> held it; only while paused.</summary>
    public void Resume()
    {
        if (State is MusicPlaybackState.Paused) State = MusicPlaybackState.Playing;
    }

    /// <summary>Silences everything at once and goes back to the start.</summary>
    public void Stop()
    {
        Rewind();
        State = MusicPlaybackState.Stopped;
    }

    /// <summary>Fills <paramref name="interleavedStereo"/> (left, right, left, …) with the next frames.</summary>
    /// <remarks>
    /// While paused or stopped it writes silence and time stands still. A single pass that ends inside the buffer
    /// stops there and leaves the rest silent; its position then stays at <see cref="Length"/> until the next
    /// <see cref="Play"/> or <see cref="Stop"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">The buffer holds a partial frame.</exception>
    public void Render(Span<float> interleavedStereo)
    {
        if ((interleavedStereo.Length & 1) != 0)
            throw new ArgumentException("The buffer must contain complete stereo frames.", nameof(interleavedStereo));
        if (State is not MusicPlaybackState.Playing)
        {
            interleavedStereo.Clear();
            return;
        }

        var wanted = interleavedStereo.Length / Channels;
        var written = 0;
        while (written < wanted && (repeat || played < LengthFrames))
        {
            if (served == Sequencer.Block)
            {
                synth.Render(blockLeft, blockRight);
                served = 0;
            }
            var take = Math.Min(Sequencer.Block - served, wanted - written);
            if (!repeat) take = (int)Math.Min(take, LengthFrames - played);
            var target = interleavedStereo.Slice(written * Channels, take * Channels);
            for (var i = 0; i < take; i++)
            {
                target[2 * i] = blockLeft[served + i] * gain;
                target[2 * i + 1] = blockRight[served + i] * gain;
            }
            served += take;
            played += take;
            written += take;
        }
        interleavedStereo[(written * Channels)..].Clear();

        if (!repeat && played == LengthFrames)
        {
            State = MusicPlaybackState.Stopped;
            Position = Length;
        }
        else
        {
            var frame = repeat ? played % LengthFrames : played;
            Position = TimeSpan.FromTicks(Math.Min(Length.Ticks, (long)(frame * (double)TimeSpan.TicksPerSecond / SampleRate)));
        }
    }

    private void Rewind()
    {
        synth.Reset(repeat);
        served = Sequencer.Block;
        played = 0;
        Position = TimeSpan.Zero;
    }

    private static Song Audible(Song song, string name) =>
        (long)(song.Length * SampleRate) >= 1 ? song : throw new InvalidDataException($"{name} lasts less than one output frame.");
}
