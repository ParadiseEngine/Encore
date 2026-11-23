namespace Encore.Audio.Music.Formats;

/// <summary>
/// Synthesizer stand-ins for the General MIDI sound set: one patch for each family of eight programs (with a
/// few programs of their own), and a drum kit for channel 10 keyed by note. Each names the part it plays, so
/// the mixer can arrange MIDI songs. They aim to be recognisable, not to imitate any sound module.
/// </summary>
public static class GeneralMidi
{
    /// <summary>The one effect bus MIDI songs send to: a medium room.</summary>
    public static EffectBus Room { get; } = new() { ReverbSeconds = 1.8, ReverbSize = 0.6, ReverbDamping = 7000 };

    /// <summary>The patch for program <paramref name="program"/> (0 to 127, as MIDI numbers them from 0).</summary>
    public static Instrument Melodic(int program)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(program);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(program, 127);
        return (program / 8) switch
        {
            0 => Piano,
            1 => Bell,
            2 => Organ,
            3 when program is 29 or 30 => DistortedGuitar,
            3 => Guitar,
            4 => Bass,
            5 when program == 45 => Pluck with { Gain = 0.3 }, // pizzicato strings
            5 when program == 46 => Pluck with { Waveform = Waveform.Triangle, Gain = 0.35 }, // harp
            5 when program == 47 => Timpani,
            5 or 6 => program is >= 52 and <= 54 ? Choir : Strings,
            7 => Brass,
            8 => Reed,
            9 => Pipe,
            10 => program == 80 ? SquareLead : SawLead,
            11 => SynthPad,
            12 => Atmosphere,
            13 when program >= 109 => Reed, // bagpipe, fiddle, shanai
            13 => Pluck,
            14 when program <= 114 => Bell with { Envelope = Envelope.Hit(0.5) }, // tinkle bell, agogo, steel drums
            14 => Timpani with { Part = ScorePart.Untagged, Envelope = Envelope.Hit(0.3) },
            _ => Effect,
        };
    }

    /// <summary>The kit's sound for note <paramref name="key"/> on channel 10.</summary>
    public static Instrument Drum(int key)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key, 127);
        return key switch
        {
            35 or 36 => Kick,
            37 => SideStick,
            38 or 40 => Snare,
            39 => Clap,
            41 or 43 or 45 or 47 or 48 or 50 => Tom with { DefaultPitch = key + 2 },
            42 or 44 => ClosedHat,
            46 => OpenHat,
            49 or 52 or 55 or 57 => Crash,
            51 or 53 or 59 => Ride,
            54 or 69 or 70 or 82 => Shaker,
            >= 60 and <= 68 => Hand with { DefaultPitch = 50 + (key - 60) * 2 },
            _ => Block with { DefaultPitch = Math.Clamp(key + 12, 0, 127) },
        };
    }

    private static Filter Lowpass(double cutoff, double resonance = 0.7071) => new(FilterMode.Lowpass, cutoff) { Resonance = resonance };

    private static Filter Highpass(double cutoff) => new(FilterMode.Highpass, cutoff);

    private static Filter Bandpass(double cutoff, double resonance) => new(FilterMode.Bandpass, cutoff) { Resonance = resonance };

    private static Sends Reverb(double amount) => new(0) { Reverb = amount };

    /// <summary>The first filter's cutoff opening by <paramref name="octaves"/> as a note starts, closing over <paramref name="decay"/> seconds.</summary>
    private static Modulation Opening(double octaves, double decay) =>
        Modulation.ByEnvelope(ModulationTarget.Cutoff, octaves, new Envelope(0.002, decay, 0, decay));

    /// <summary>A pitch falling <paramref name="semitones"/> to the note over <paramref name="decay"/> seconds: a drum's thump.</summary>
    private static Modulation Drop(double semitones, double decay) =>
        Modulation.ByEnvelope(ModulationTarget.Pitch, semitones, Envelope.Hit(decay));

    private static Modulation Vibrato(double rate, double semitones) => Modulation.ByLfo(ModulationTarget.Pitch, semitones, rate);

    private static readonly Instrument Piano = new()
    {
        Name = "piano", Part = ScorePart.Keys, Waveform = Waveform.Triangle, Unison = new Unison(2, 0.06, 0),
        Gain = 0.4, Envelope = new(0.002, 1.6, 0.12, 0.3), Filters = [Lowpass(1800, 0.9)], Modulations = [Opening(1.5, 0.5)], Sends = Reverb(0.15),
    };

    private static readonly Instrument Bell = new()
    {
        Name = "bell", Part = ScorePart.Keys, Waveform = Waveform.Sine, Gain = 0.35, Fm = new Fm(3.5, 0),
        Modulations = [Modulation.ByEnvelope(ModulationTarget.FmIndex, 1.6, new Envelope(0.001, 0.8, 0, 0.3))],
        Envelope = new(0.001, 1.4, 0, 0.4), Sends = Reverb(0.25),
    };

    private static readonly Instrument Organ = new()
    {
        Name = "organ", Part = ScorePart.Keys, Waveform = Waveform.Square, Gain = 0.16, Envelope = new(0.008, 0, 1, 0.06),
        Filters = [Lowpass(2400)], Modulations = [Modulation.ByLfo(ModulationTarget.Amplitude, 0.15, 5.5)], Sends = Reverb(0.2),
    };

    private static readonly Instrument Guitar = new()
    {
        Name = "guitar", Part = ScorePart.Keys, Waveform = Waveform.Sawtooth, Gain = 0.28,
        Envelope = new(0.002, 0.9, 0.1, 0.2), Filters = [Lowpass(1200, 0.9)], Modulations = [Opening(2, 0.25)], Sends = Reverb(0.12),
    };

    private static readonly Instrument DistortedGuitar = Guitar with
    {
        Name = "distorted guitar", Drive = 2.5, Gain = 0.18, Envelope = new(0.002, 0.4, 0.7, 0.15), Filters = [Lowpass(2800, 1.2)], Modulations = [],
    };

    private static readonly Instrument Bass = new()
    {
        Name = "bass", Part = ScorePart.Bass, Waveform = Waveform.Sawtooth, Gain = 0.38,
        Envelope = new(0.003, 0.4, 0.6, 0.08), Filters = [Lowpass(350, 1.4)], Modulations = [Opening(2, 0.15)],
    };

    private static readonly Instrument Pluck = new()
    {
        Name = "pluck", Part = ScorePart.Keys, Waveform = Waveform.Sawtooth, Gain = 0.25,
        Envelope = Envelope.Hit(0.5), Filters = [Lowpass(900, 0.9)], Modulations = [Opening(2.5, 0.2)], Sends = Reverb(0.15),
    };

    private static readonly Instrument Timpani = new()
    {
        Name = "timpani", Part = ScorePart.Kick, Waveform = Waveform.Sine, Gain = 0.5,
        Envelope = Envelope.Hit(0.9), Modulations = [Drop(3, 0.08)], Noise = 0.05, Sends = Reverb(0.25),
    };

    private static readonly Instrument Strings = new()
    {
        Name = "strings", Part = ScorePart.Pad, Waveform = Waveform.Sawtooth, Unison = new Unison(3, 0.14, 0.5),
        Gain = 0.15, Envelope = new(0.18, 0.3, 0.85, 0.45), Filters = [Lowpass(3200)], Modulations = [Vibrato(5, 0.06)], Sends = Reverb(0.3),
    };

    private static readonly Instrument Choir = Strings with { Name = "choir", Waveform = Waveform.Triangle, Gain = 0.25, Filters = [Bandpass(900, 0.8)] };

    private static readonly Instrument Brass = new()
    {
        Name = "brass", Part = ScorePart.Melody, Waveform = Waveform.Sawtooth, Gain = 0.2, Envelope = new(0.03, 0.2, 0.8, 0.15),
        Filters = [Lowpass(900)], Modulations = [Modulation.ByEnvelope(ModulationTarget.Cutoff, 1.6, new Envelope(0.05, 0.3, 0.5, 0.15))],
        Sends = Reverb(0.15),
    };

    private static readonly Instrument Reed = new()
    {
        Name = "reed", Part = ScorePart.Melody, Waveform = Waveform.Square, Gain = 0.17, Envelope = new(0.02, 0.1, 0.9, 0.1),
        Filters = [Lowpass(1800, 1.1)], Modulations = [Vibrato(5.2, 0.08)], Sends = Reverb(0.15),
    };

    private static readonly Instrument Pipe = new()
    {
        Name = "pipe", Part = ScorePart.Melody, Waveform = Waveform.Triangle, Gain = 0.32, Noise = 0.04,
        Envelope = new(0.04, 0.1, 0.9, 0.12), Modulations = [Vibrato(5, 0.1)], Sends = Reverb(0.2),
    };

    private static readonly Instrument SquareLead = new()
    {
        Name = "square lead", Part = ScorePart.Melody, Waveform = Waveform.Pulse, PulseWidth = 0.4, Gain = 0.16,
        Envelope = new(0.005, 0.1, 0.9, 0.1), Filters = [Lowpass(3500)], Sends = Reverb(0.12),
    };

    private static readonly Instrument SawLead = SquareLead with { Name = "saw lead", Waveform = Waveform.Sawtooth, Gain = 0.15 };

    private static readonly Instrument SynthPad = new()
    {
        Name = "pad", Part = ScorePart.Pad, Waveform = Waveform.Sawtooth, Unison = new Unison(5, 0.2, 0.7),
        Gain = 0.1, Envelope = new(0.4, 0.5, 0.8, 0.8), Filters = [Lowpass(1800)],
        Modulations = [Modulation.ByLfo(ModulationTarget.Cutoff, 0.5, 0.2)], Sends = Reverb(0.4),
    };

    private static readonly Instrument Atmosphere = new()
    {
        Name = "atmosphere", Part = ScorePart.Pad, Waveform = Waveform.Triangle, Unison = new Unison(3, 0.12, 0.8),
        Gain = 0.16, Envelope = new(0.3, 0.5, 0.7, 1.0), Phaser = new Phaser(0.25, 0.6, 1000, 800), Sends = Reverb(0.45),
    };

    private static readonly Instrument Effect = new()
    {
        Name = "effect", Part = ScorePart.Untagged, Waveform = Waveform.WhiteNoise, Gain = 0.1, Envelope = new(0.05, 0.5, 0.5, 0.5),
        Filters = [Bandpass(1500, 2)], Modulations = [Modulation.ByLfo(ModulationTarget.Cutoff, 1, 0.5)], Sends = Reverb(0.3),
    };

    private static readonly Instrument Kick = new()
    {
        Name = "kick", Part = ScorePart.Kick, Waveform = Waveform.Sine, DefaultPitch = 33, Gain = 0.8,
        Envelope = new(0.001, 0.35, 0, 0.05), Modulations = [Drop(24, 0.04)],
    };

    private static readonly Instrument Snare = new()
    {
        Name = "snare", Part = ScorePart.Snare, Waveform = Waveform.Triangle, DefaultPitch = 50, Noise = 0.75, Gain = 0.45,
        Envelope = Envelope.Hit(0.18), Modulations = [Drop(7, 0.03)], Filters = [Lowpass(7000)], Sends = Reverb(0.15),
    };

    private static readonly Instrument SideStick = new()
    {
        Name = "side stick", Part = ScorePart.Snare, Waveform = Waveform.Triangle, DefaultPitch = 76, Noise = 0.3, Gain = 0.3,
        Envelope = Envelope.Hit(0.04),
    };

    private static readonly Instrument Clap = new()
    {
        Name = "clap", Part = ScorePart.Snare, Waveform = Waveform.WhiteNoise, Gain = 0.45,
        Envelope = Envelope.Hit(0.14), Filters = [Bandpass(1200, 1.4)], Sends = Reverb(0.2),
    };

    private static readonly Instrument Tom = new()
    {
        Name = "tom", Part = ScorePart.Snare, Waveform = Waveform.Sine, Gain = 0.5,
        Envelope = Envelope.Hit(0.28), Modulations = [Drop(7, 0.06)], Sends = Reverb(0.1),
    };

    private static readonly Instrument ClosedHat = new()
    {
        Name = "closed hat", Part = ScorePart.Hats, Waveform = Waveform.WhiteNoise, Gain = 0.22,
        Envelope = Envelope.Hit(0.05), Filters = [Highpass(7000)],
    };

    private static readonly Instrument OpenHat = ClosedHat with { Name = "open hat", Gain = 0.18, Envelope = Envelope.Hit(0.35), Filters = [Highpass(6500)] };

    private static readonly Instrument Crash = ClosedHat with { Name = "crash", Gain = 0.15, Envelope = Envelope.Hit(1.3), Filters = [Highpass(4000)], Sends = Reverb(0.2) };

    private static readonly Instrument Ride = ClosedHat with { Name = "ride", Gain = 0.1, Envelope = Envelope.Hit(0.7), Filters = [Highpass(5000), Bandpass(9000, 0.8)] };

    private static readonly Instrument Shaker = ClosedHat with { Name = "shaker", Gain = 0.14, Envelope = new(0.01, 0.08, 0, 0.03), Filters = [Highpass(5000)] };

    private static readonly Instrument Hand = new()
    {
        Name = "hand drum", Part = ScorePart.Snare, Waveform = Waveform.Sine, Gain = 0.4,
        Envelope = Envelope.Hit(0.2), Modulations = [Drop(5, 0.03)], Noise = 0.08,
    };

    private static readonly Instrument Block = new()
    {
        Name = "percussion", Part = ScorePart.Untagged, Waveform = Waveform.Triangle, Gain = 0.25,
        Envelope = Envelope.Hit(0.08), Filters = [Bandpass(2000, 1.5)],
    };
}
