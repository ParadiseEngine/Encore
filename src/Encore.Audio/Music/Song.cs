namespace Encore.Audio.Music;

/// <summary>What a voice's oscillator plays: a periodic wave, or a noise.</summary>
public enum Waveform : byte
{
    Sine,
    Triangle,
    Sawtooth,
    Square,
    /// <summary>A rectangle wave of <see cref="Instrument.PulseWidth"/>.</summary>
    Pulse,
    WhiteNoise,
    PinkNoise,
    BrownNoise,
}

/// <summary>Which band a <see cref="Filter"/> lets through.</summary>
public enum FilterMode : byte { Lowpass, Highpass, Bandpass }

/// <summary>The shape a <see cref="Lfo"/> repeats.</summary>
public enum LfoShape : byte { Sine, Triangle, Square, Saw }

/// <summary>What a <see cref="Modulation"/> moves, and in what unit its amount is.</summary>
public enum ModulationTarget : byte
{
    /// <summary>The pitch, in semitones.</summary>
    Pitch,
    /// <summary>The cutoff of the instrument's filter number <see cref="Modulation.Filter"/>, in octaves.</summary>
    Cutoff,
    /// <summary>The loudness: scaled by 1 − amount × (1 − level), level the source's 0-to-1 value. An LFO at amount 0.3 is a tremolo dipping to 0.7.</summary>
    Amplitude,
    /// <summary>The pulse width (added; the result stays within 0.02 to 0.98).</summary>
    PulseWidth,
    /// <summary>The FM index (added to <see cref="Fm.Index"/>; the result is not negative).</summary>
    FmIndex,
}

/// <summary>
/// An attack-decay-sustain-release contour that peaks at 1: <paramref name="Attack"/>, <paramref name="Decay"/>
/// and <paramref name="Release"/> in seconds, <paramref name="Sustain"/> the level (0 to 1) held while the note lasts.
/// </summary>
public readonly record struct Envelope(double Attack, double Decay, double Sustain, double Release)
{
    /// <summary>A short fade in and out around a held note.</summary>
    public static Envelope Held => new(0.005, 0, 1, 0.05);

    /// <summary>An instant rise that dies away over <paramref name="decay"/> seconds, whatever the note's length.</summary>
    public static Envelope Hit(double decay) => new(0.001, decay, 0, Math.Min(decay, 0.05));
}

/// <summary>A low-frequency oscillator: <paramref name="Rate"/> in hertz, swinging −1 to 1 from <see cref="Phase"/> (0 to 1) at the note's start.</summary>
public sealed record Lfo(double Rate)
{
    public LfoShape Shape { get; init; } = LfoShape.Sine;

    public double Phase { get; init; }
}

/// <summary>
/// One route of an instrument's modulation matrix: an <see cref="Lfo"/> (swinging −1 to 1) or an
/// <see cref="Envelope"/> (0 to 1, gated by the note) moving <paramref name="Target"/> by up to
/// <paramref name="Amount"/> (in the target's unit). Exactly one of <see cref="Lfo"/> and <see cref="Envelope"/> is set.
/// </summary>
public sealed record Modulation(ModulationTarget Target, double Amount)
{
    public Lfo? Lfo { get; init; }

    public Envelope? Envelope { get; init; }

    /// <summary>For <see cref="ModulationTarget.Cutoff"/>: the index of the filter in <see cref="Instrument.Filters"/>.</summary>
    public int Filter { get; init; }

    public static Modulation ByLfo(ModulationTarget target, double amount, double rate, LfoShape shape = LfoShape.Sine) =>
        new(target, amount) { Lfo = new Lfo(rate) { Shape = shape } };

    public static Modulation ByEnvelope(ModulationTarget target, double amount, Envelope envelope) =>
        new(target, amount) { Envelope = envelope };
}

/// <summary>A resonant filter: <paramref name="Cutoff"/> in hertz; <see cref="Modulation"/>s with <see cref="ModulationTarget.Cutoff"/> move it.</summary>
public sealed record Filter(FilterMode Mode, double Cutoff)
{
    /// <summary>The filter's Q: 0.707 is flat, higher rings at the cutoff.</summary>
    public double Resonance { get; init; } = 0.7071;

    /// <summary>Two stages (24 dB an octave) rather than one (12 dB).</summary>
    public bool Steep { get; init; }
}

/// <summary>Several detuned copies of the oscillator: <paramref name="Voices"/> of them spanning <paramref name="Detune"/> semitones, spread across the stereo field by <paramref name="Spread"/> (0 centre, 1 both sides).</summary>
public sealed record Unison(int Voices, double Detune, double Spread);

/// <summary>Frequency modulation by a sine at <paramref name="Ratio"/> times the note's frequency, <paramref name="Index"/> its deviation in multiples of the modulator's frequency.</summary>
public sealed record Fm(double Ratio, double Index);

/// <summary>A sweeping notch: <paramref name="Rate"/> in hertz, <paramref name="Depth"/> 0 to 1, the notch moving <paramref name="Sweep"/> hertz either side of <paramref name="Center"/>.</summary>
public sealed record Phaser(double Rate, double Depth, double Center, double Sweep);

/// <summary>Where a voice goes after panning: <see cref="Dry"/> straight out, and shares to the <see cref="Song.Effects"/> bus number <paramref name="Bus"/>.</summary>
public sealed record Sends(int Bus)
{
    public double Dry { get; init; } = 1;

    public double Reverb { get; init; }

    public double Delay { get; init; }
}

/// <summary>
/// A synthesizer patch every note of a <see cref="Song"/> names. A voice runs the oscillator (stacked by
/// <see cref="Unison"/>, frequency-modulated by <see cref="Fm"/>, with <see cref="Noise"/> blended in)
/// through the <see cref="Filters"/> in order, <see cref="Drive"/>, the <see cref="Phaser"/> and the
/// amplitude <see cref="Envelope"/>, pans, and goes out through its <see cref="Sends"/>. The
/// <see cref="Modulations"/> move pitch, cutoffs, loudness, pulse width and FM index as the note plays.
/// </summary>
public sealed record Instrument
{
    /// <summary>A name for people; the player ignores it.</summary>
    public string Name { get; init; } = "";

    /// <summary>The role it plays, by which the mixer arranges the music.</summary>
    public ScorePart Part { get; init; }

    public Waveform Waveform { get; init; } = Waveform.Triangle;

    /// <summary>The share of each cycle a <see cref="Waveform.Pulse"/> spends high, in (0, 1).</summary>
    public double PulseWidth { get; init; } = 0.5;

    public Unison? Unison { get; init; }

    public Fm? Fm { get; init; }

    /// <summary>White noise blended into an oscillator, 0 to 1.</summary>
    public double Noise { get; init; }

    /// <summary>The amplitude contour of each note.</summary>
    public Envelope Envelope { get; init; } = Envelope.Held;

    /// <summary>The filters each voice runs through, in order.</summary>
    public IReadOnlyList<Filter> Filters { get; init; } = [];

    /// <summary>Soft saturation, 0 (clean) upward.</summary>
    public double Drive { get; init; }

    public Phaser? Phaser { get; init; }

    public IReadOnlyList<Modulation> Modulations { get; init; } = [];

    /// <summary>The instrument's level: a voice's output peaks near gain × velocity.</summary>
    public double Gain { get; init; } = 1;

    /// <summary>−1 (left) to 1 (right), at constant power.</summary>
    public double Pan { get; init; }

    /// <summary>Its effect sends; null is dry only.</summary>
    public Sends? Sends { get; init; }

    /// <summary>The pitch (a MIDI note number) of a note that names none: drums and noises often don't.</summary>
    public double DefaultPitch { get; init; } = 60;
}

/// <summary>The shared effects instruments send to: a reverb and a feedback delay, each made only when something sends to it.</summary>
public sealed record EffectBus
{
    /// <summary>Seconds the reverb takes to die away by 60 dB.</summary>
    public double ReverbSeconds { get; init; } = 1.6;

    /// <summary>The reverb's room size, 0 (small) to 1 (large).</summary>
    public double ReverbSize { get; init; } = 0.5;

    /// <summary>The frequency, in hertz, above which the reverb's tail darkens.</summary>
    public double ReverbDamping { get; init; } = 9000;

    /// <summary>The delay's echo time in seconds, up to <see cref="Song.MaxDelaySeconds"/>.</summary>
    public double DelaySeconds { get; init; } = 0.25;

    /// <summary>How much of each echo comes back, 0 to 0.95.</summary>
    public double DelayFeedback { get; init; } = 0.35;

    /// <summary>The frequency, in hertz, above which each echo darkens.</summary>
    public double DelayDamping { get; init; } = 6000;
}

/// <summary>
/// One note: from <paramref name="Start"/> for <paramref name="Duration"/> seconds (its release follows), at
/// <paramref name="Pitch"/> (a MIDI note number, fractions allowed; null is the instrument's
/// <see cref="Instrument.DefaultPitch"/>) and <paramref name="Velocity"/> (0 to 1), on the song's instrument
/// number <paramref name="Instrument"/>.
/// </summary>
public readonly record struct Note(double Start, double Duration, double? Pitch, float Velocity, int Instrument);

/// <summary>
/// A piece of music as the player plays it, whatever format it came from (see <see cref="Formats.MusicFormats"/>):
/// instruments, the notes they play in seconds, the effect buses they send to, and its length. Looping
/// starts every note again each <see cref="Length"/>. Immutable once made.
/// </summary>
public sealed class Song
{
    public const int MaxUnison = 16;
    public const int MaxFilters = 4;
    public const int MaxModulations = 8;
    public const double MaxDelaySeconds = 4;
    public const double MaxLength = 24 * 3600;

    /// <exception cref="ArgumentException">A value is out of its range, or a note names an instrument (or an instrument a bus) that is not there.</exception>
    public Song(IEnumerable<Instrument> instruments, IEnumerable<Note> notes, double length, IEnumerable<EffectBus>? effects = null, uint seed = 1, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(notes);
        if (!(length > 0) || length > MaxLength)
            throw new ArgumentException($"A song lasts more than 0 and at most {MaxLength} seconds.", nameof(length));
        Instrument[] instrumentArray = [.. instruments];
        EffectBus[] effectArray = effects is null ? [] : [.. effects];
        Note[] noteArray = [.. notes];
        foreach (var bus in effectArray)
            Validate(bus);
        foreach (var instrument in instrumentArray)
            Validate(instrument, effectArray.Length);
        foreach (var note in noteArray)
        {
            if (!(note.Start >= 0) || !(note.Start < length))
                throw new ArgumentException($"A note starts within the song: [0, {length}) seconds, not {note.Start}.", nameof(notes));
            if (!(note.Duration >= 0) || !double.IsFinite(note.Duration))
                throw new ArgumentException("A note's duration is finite and not negative.", nameof(notes));
            if (!(note.Velocity is >= 0f and <= 1f))
                throw new ArgumentException("A note's velocity lies in [0, 1].", nameof(notes));
            if (note.Pitch is { } pitch && !(pitch is >= -60 and <= 160))
                throw new ArgumentException("A note's pitch lies in [-60, 160].", nameof(notes));
            if ((uint)note.Instrument >= (uint)instrumentArray.Length)
                throw new ArgumentException($"A note names instrument {note.Instrument}, which the song does not have.", nameof(notes));
        }
        // A stable sort: notes that start together keep their order.
        var order = new int[noteArray.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => noteArray[a].Start != noteArray[b].Start ? noteArray[a].Start.CompareTo(noteArray[b].Start) : a.CompareTo(b));
        var sorted = new Note[noteArray.Length];
        for (var i = 0; i < order.Length; i++) sorted[i] = noteArray[order[i]];

        Instruments = instrumentArray;
        Notes = sorted;
        Effects = effectArray;
        Length = length;
        Seed = seed;
        Title = title;
    }

    public IReadOnlyList<Instrument> Instruments { get; }

    /// <summary>The notes by start time.</summary>
    public IReadOnlyList<Note> Notes { get; }

    public IReadOnlyList<EffectBus> Effects { get; }

    /// <summary>Seconds from the start to where a loop starts again (and a single play ends).</summary>
    public double Length { get; }

    /// <summary>Seeds the noises and the unison oscillators' phases: the same song renders the same samples.</summary>
    public uint Seed { get; }

    public string? Title { get; }

    private static void Validate(EffectBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        Require(bus.ReverbSeconds is > 0 and <= 60, "An effect bus's reverb time lies in (0, 60] seconds.");
        Require(bus.ReverbSize is >= 0 and <= 1, "An effect bus's reverb size lies in [0, 1].");
        Require(Frequency(bus.ReverbDamping), "An effect bus's reverb damping is a positive frequency.");
        Require(bus.DelaySeconds is > 0 and <= MaxDelaySeconds, $"An effect bus's delay lies in (0, {MaxDelaySeconds}] seconds.");
        Require(bus.DelayFeedback is >= 0 and <= 0.95, "An effect bus's delay feedback lies in [0, 0.95].");
        Require(Frequency(bus.DelayDamping), "An effect bus's delay damping is a positive frequency.");
    }

    private static void Validate(Instrument instrument, int buses)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Require((int)instrument.Part < ScoreParts.Count, "An instrument's part is a ScorePart.");
        Require(instrument.Waveform <= Waveform.BrownNoise, "An instrument's waveform is a Waveform.");
        Require(instrument.PulseWidth is > 0 and < 1, "An instrument's pulse width lies in (0, 1).");
        if (instrument.Unison is { } unison)
        {
            Require(unison.Voices is >= 1 and <= MaxUnison, $"A unison has 1 to {MaxUnison} voices.");
            Require(unison.Detune is >= 0 and <= 24, "A unison's detune lies in [0, 24] semitones.");
            Require(unison.Spread is >= 0 and <= 1, "A unison's spread lies in [0, 1].");
        }
        if (instrument.Fm is { } fm)
            Require(fm.Ratio is > 0 and <= 64 && fm.Index is >= 0 and <= 100, "FM's ratio lies in (0, 64] and its index in [0, 100].");
        Require(instrument.Noise is >= 0 and <= 1, "An instrument's noise lies in [0, 1].");
        Validate(instrument.Envelope);
        ArgumentNullException.ThrowIfNull(instrument.Filters);
        Require(instrument.Filters.Count <= MaxFilters, $"An instrument has at most {MaxFilters} filters.");
        foreach (var filter in instrument.Filters)
        {
            ArgumentNullException.ThrowIfNull(filter);
            Require(filter.Mode <= FilterMode.Bandpass, "A filter's mode is a FilterMode.");
            Require(Frequency(filter.Cutoff), "A filter's cutoff is a positive frequency.");
            Require(filter.Resonance is >= 0.1 and <= 50, "A filter's resonance lies in [0.1, 50].");
        }
        Require(NonNegative(instrument.Drive) && instrument.Drive <= 100, "An instrument's drive lies in [0, 100].");
        if (instrument.Phaser is { } phaser)
            Require(NonNegative(phaser.Rate) && phaser.Depth is >= 0 and <= 1 && Frequency(phaser.Center) && NonNegative(phaser.Sweep),
                "A phaser's rate and sweep are not negative, its depth lies in [0, 1] and its centre is a positive frequency.");
        ArgumentNullException.ThrowIfNull(instrument.Modulations);
        Require(instrument.Modulations.Count <= MaxModulations, $"An instrument has at most {MaxModulations} modulations.");
        foreach (var modulation in instrument.Modulations)
        {
            ArgumentNullException.ThrowIfNull(modulation);
            Require(modulation.Target <= ModulationTarget.FmIndex, "A modulation's target is a ModulationTarget.");
            Require((modulation.Lfo is null) != (modulation.Envelope is null), "A modulation has either an LFO or an envelope.");
            Require(double.IsFinite(modulation.Amount) && Math.Abs(modulation.Amount) <= 100, "A modulation's amount lies in [-100, 100].");
            if (modulation.Target == ModulationTarget.Cutoff)
                Require((uint)modulation.Filter < (uint)instrument.Filters.Count, $"A cutoff modulation names filter {modulation.Filter}, which the instrument does not have.");
            if (modulation.Lfo is { } lfo)
                Require(NonNegative(lfo.Rate) && lfo.Rate <= 1000 && lfo.Shape <= LfoShape.Saw && lfo.Phase is >= 0 and < 1,
                    "An LFO's rate lies in [0, 1000] hertz, its shape is an LfoShape and its phase lies in [0, 1).");
            if (modulation.Envelope is { } envelope) Validate(envelope);
        }
        Require(NonNegative(instrument.Gain), "An instrument's gain is finite and not negative.");
        Require(instrument.Pan is >= -1 and <= 1, "An instrument's pan lies in [-1, 1].");
        if (instrument.Sends is { } sends)
        {
            Require(NonNegative(sends.Dry) && NonNegative(sends.Reverb) && NonNegative(sends.Delay), "An instrument's sends are finite and not negative.");
            if (sends.Reverb > 0 || sends.Delay > 0)
                Require((uint)sends.Bus < (uint)buses, $"An instrument sends to effect bus {sends.Bus}, which the song does not have.");
        }
        Require(instrument.DefaultPitch is >= -60 and <= 160, "An instrument's default pitch lies in [-60, 160].");
    }

    private static void Validate(Envelope envelope)
    {
        Require(NonNegative(envelope.Attack) && NonNegative(envelope.Decay) && NonNegative(envelope.Release)
            && envelope.Attack <= 600 && envelope.Decay <= 600 && envelope.Release <= 600,
            "An envelope's times lie in [0, 600] seconds.");
        Require(envelope.Sustain is >= 0 and <= 1, "An envelope's sustain lies in [0, 1].");
    }

    private static bool NonNegative(double value) => value >= 0 && double.IsFinite(value);

    private static bool Frequency(double value) => value > 0 && double.IsFinite(value);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
