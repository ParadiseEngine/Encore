namespace Encore.Audio.Music.Synth;

/// <summary>The engine's rate and the conversions every part of it shares.</summary>
internal static class Dsp
{
    public const int SampleRate = 48_000;

    /// <summary>The highest frequency anything is tuned to: a little under Nyquist.</summary>
    public const double MaxFrequency = 0.45 * SampleRate;

    /// <summary>The frequency of a MIDI note number (69 is A 440 Hz).</summary>
    public static double MidiToHz(double pitch) => 440 * Math.Pow(2, (pitch - 69) / 12);

    /// <summary>The pole of a one-pole lowpass at <paramref name="cutoff"/> hertz: y += (x − y)(1 − a); 0 (no filtering) from <see cref="MaxFrequency"/> up.</summary>
    public static float OnePole(double cutoff) => cutoff >= MaxFrequency ? 0f : (float)Math.Exp(-2 * Math.PI * cutoff / SampleRate);

    /// <summary>The fractional part, so a phase stays in [0, 1) whichever way it moves.</summary>
    public static double Wrap(double phase) => phase - Math.Floor(phase);

    /// <summary>Zero for values too small to hear, so feedback paths never sink into subnormal floats.</summary>
    public static float Flush(float value) => MathF.Abs(value) < 1e-20f ? 0f : value;
}

/// <summary>A sine table, linearly interpolated: cheaper than Math.Sin for every oscillator sample.</summary>
internal static class SineTable
{
    private const int Size = 4096;
    private static readonly float[] Table = Build();

    /// <summary>sin(2π <paramref name="phase"/>) for a phase in [0, 1).</summary>
    public static float Sin(double phase)
    {
        var x = phase * Size;
        var i = (int)x;
        if ((uint)i >= Size) i = (i % Size + Size) % Size;
        var a = Table[i];
        return a + (Table[i + 1] - a) * (float)(x - Math.Floor(x));
    }

    private static float[] Build()
    {
        var table = new float[Size + 1];
        for (var i = 0; i <= Size; i++)
            table[i] = (float)Math.Sin(2 * Math.PI * i / Size);
        return table;
    }
}

/// <summary>SplitMix64: a small, fast generator whose stream depends only on its seed.</summary>
internal struct Rng(ulong seed)
{
    private ulong state = seed;

    public ulong Next()
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A double in [0, 1).</summary>
    public double NextUnit() => (Next() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A float in [−1, 1).</summary>
    public float NextBipolar() => (Next() >> 40) * (2f / (1 << 24)) - 1f;

    /// <summary>Folds several values into one well-mixed seed.</summary>
    public static ulong Seed(ulong a, ulong b, ulong c)
    {
        var rng = new Rng(a ^ (b * 0xD1B54A32D192ED03UL) ^ (c * 0xABC98388FB8FAC03UL));
        return rng.Next();
    }
}

/// <summary>
/// An ADSR contour that peaks at 1, advanced a sample at a time: a straight attack, then decay and release
/// along a curve that falls fast and settles (an exponential stretched to land exactly on its target in the
/// segment's time).
/// </summary>
internal struct EnvelopeState
{
    private const double Steepness = 5;
    private static readonly double CurveEnd = Math.Exp(-Steepness);

    private Stage stage;
    private double level, attackStep, decayRatio, releaseRatio, sustain, curve, from;

    private enum Stage : byte { Attack, Decay, Sustain, Release, Done }

    /// <summary>The contour has ended at 0: an amplitude envelope's voice is silent for good.</summary>
    public readonly bool Done => stage == Stage.Done;

    public readonly bool Releasing => stage is Stage.Release or Stage.Done;

    public readonly double Level => level;

    /// <summary>Starts at 0 (or at once at 1 for an attack shorter than a sample); a release is at least <paramref name="minimumRelease"/> seconds.</summary>
    public void Start(Envelope envelope, double minimumRelease)
    {
        sustain = envelope.Sustain;
        var attackFrames = envelope.Attack * Dsp.SampleRate;
        attackStep = attackFrames >= 1 ? 1 / attackFrames : 0;
        decayRatio = Ratio(envelope.Decay);
        releaseRatio = Ratio(Math.Max(envelope.Release, minimumRelease));
        level = 0;
        stage = Stage.Attack;
        if (attackStep == 0)
        {
            level = 1;
            BeginDecay();
        }
    }

    /// <summary>The note has ended: fall from wherever the contour is.</summary>
    public void Release()
    {
        if (stage is Stage.Release or Stage.Done) return;
        from = level;
        curve = 1;
        stage = Stage.Release;
        if (releaseRatio == 0 || from <= 0)
        {
            level = 0;
            stage = Stage.Done;
        }
    }

    /// <summary>Advances a sample and returns the level reached.</summary>
    public double Next()
    {
        switch (stage)
        {
            case Stage.Attack:
                level += attackStep;
                if (level >= 1)
                {
                    level = 1;
                    BeginDecay();
                }
                break;
            case Stage.Decay:
                curve *= decayRatio;
                if (curve <= CurveEnd) EnterSustain();
                else level = sustain + (1 - sustain) * (curve - CurveEnd) / (1 - CurveEnd);
                break;
            case Stage.Release:
                curve *= releaseRatio;
                if (curve <= CurveEnd)
                {
                    level = 0;
                    stage = Stage.Done;
                }
                else level = from * (curve - CurveEnd) / (1 - CurveEnd);
                break;
        }
        return level;
    }

    /// <summary>Returns the level now, then advances <paramref name="frames"/> samples.</summary>
    public double Advance(int frames)
    {
        var now = level;
        for (var i = 0; i < frames && stage is not (Stage.Sustain or Stage.Done); i++)
            Next();
        return now;
    }

    private void BeginDecay()
    {
        stage = Stage.Decay;
        curve = 1;
        if (decayRatio == 0) EnterSustain();
    }

    private void EnterSustain()
    {
        level = sustain;
        stage = sustain > 0 ? Stage.Sustain : Stage.Done;
    }

    private static double Ratio(double seconds)
    {
        var frames = seconds * Dsp.SampleRate;
        return frames >= 1 ? Math.Exp(-Steepness / frames) : 0;
    }
}

/// <summary>A state-variable filter's coefficients (trapezoidal integration, as in Zavalishin's "The Art of VA Filter Design").</summary>
internal readonly struct SvfCoefficients
{
    public readonly float A1, A2, A3, K;

    private SvfCoefficients(float a1, float a2, float a3, float k)
    {
        A1 = a1;
        A2 = a2;
        A3 = a3;
        K = k;
    }

    public static SvfCoefficients For(double cutoff, double q)
    {
        var g = Math.Tan(Math.PI * Math.Clamp(cutoff, 10, Dsp.MaxFrequency) / Dsp.SampleRate);
        var k = 1 / q;
        var a1 = 1 / (1 + g * (g + k));
        var a2 = g * a1;
        return new SvfCoefficients((float)a1, (float)a2, (float)(g * a2), (float)k);
    }
}

/// <summary>One channel of a state-variable filter: lowpass, highpass and (unity-peak) bandpass from the same two integrators.</summary>
internal struct SvfState
{
    private float ic1, ic2;

    public float Process(float input, in SvfCoefficients c, FilterMode mode)
    {
        var v3 = input - ic2;
        var v1 = c.A1 * ic1 + c.A2 * v3;
        var v2 = ic2 + c.A2 * ic1 + c.A3 * v3;
        ic1 = 2 * v1 - ic1;
        ic2 = 2 * v2 - ic2;
        return mode switch
        {
            FilterMode.Lowpass => v2,
            FilterMode.Highpass => input - c.K * v1 - v2,
            _ => c.K * v1,
        };
    }

    public void Reset() => ic1 = ic2 = 0;
}

/// <summary>A first-order allpass: y[n] = a·x[n] + x[n−1] − a·y[n−1].</summary>
internal struct AllpassState
{
    private float x1, y1;

    /// <summary>The coefficient that puts the allpass's 90° point at <paramref name="frequency"/> hertz.</summary>
    public static float Coefficient(double frequency)
    {
        var t = Math.Tan(Math.PI * Math.Clamp(frequency, 10, Dsp.MaxFrequency) / Dsp.SampleRate);
        return (float)((t - 1) / (t + 1));
    }

    public float Process(float x, float a)
    {
        var y = a * x + x1 - a * y1;
        x1 = x;
        y1 = Dsp.Flush(y);
        return y;
    }

    public void Reset() => x1 = y1 = 0;
}

/// <summary>Band-limited oscillator shapes: PolyBLEP corrections smooth the saw's and the rectangles' jumps.</summary>
internal static class Oscillator
{
    /// <summary>
    /// The waveform's value at <paramref name="phase"/> (in [0, 1)), for a phase that moves <paramref name="increment"/>
    /// a sample; <paramref name="width"/> is a pulse's duty cycle.
    /// </summary>
    public static float Sample(Waveform waveform, double phase, double increment, double width)
    {
        var dt = Math.Abs(increment);
        return waveform switch
        {
            Waveform.Sine => SineTable.Sin(phase),
            Waveform.Triangle => (float)(4 * Math.Abs(phase - 0.5) - 1),
            Waveform.Sawtooth => (float)(2 * phase - 1 - Blep(phase, dt)),
            Waveform.Square => Rectangle(phase, dt, 0.5),
            Waveform.Pulse => Rectangle(phase, dt, width),
            _ => 0f,
        };
    }

    /// <summary>A rectangle of duty cycle <paramref name="width"/> without its DC offset.</summary>
    private static float Rectangle(double phase, double dt, double width)
    {
        var value = phase < width ? 1.0 : -1.0;
        value += Blep(phase, dt);
        var falling = phase - width;
        if (falling < 0) falling += 1;
        value -= Blep(falling, dt);
        return (float)(value - (2 * width - 1));
    }

    /// <summary>The two-sample polynomial correction for a unit step at phase 0.</summary>
    private static double Blep(double t, double dt)
    {
        if (dt <= 0) return 0;
        if (t < dt)
        {
            var x = t / dt;
            return x + x - x * x - 1;
        }
        if (t > 1 - dt)
        {
            var x = (t - 1) / dt;
            return x * x + x + x + 1;
        }
        return 0;
    }
}

/// <summary>The noises a voice can make, each from the voice's own seeded generator.</summary>
internal struct NoiseState(ulong seed)
{
    private Rng rng = new(seed);
    private float pink0, pink1, pink2, brown;

    public double NextUnit() => rng.NextUnit();

    public float White() => rng.NextBipolar();

    /// <summary>White noise through three leaky integrators whose sum falls about 3 dB an octave.</summary>
    public float Pink()
    {
        var w = White();
        pink0 = 0.99765f * pink0 + w * 0.0990460f;
        pink1 = 0.96300f * pink1 + w * 0.2965164f;
        pink2 = 0.57000f * pink2 + w * 1.0526913f;
        return (pink0 + pink1 + pink2 + w * 0.1848f) * 0.22f;
    }

    /// <summary>White noise through one leaky integrator: about 6 dB an octave down.</summary>
    public float Brown()
    {
        brown = (brown + 0.02f * White()) / 1.02f;
        return brown * 3.5f;
    }
}
