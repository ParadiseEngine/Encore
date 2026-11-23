namespace Encore.Audio.Music.Synth;

/// <summary>A filter's settings worked out once for every voice that runs it.</summary>
internal sealed class FilterPlan(Filter filter, bool moves)
{
    public FilterMode Mode { get; } = filter.Mode;
    public double Cutoff { get; } = filter.Cutoff;
    public double Resonance { get; } = filter.Resonance;
    public bool Steep { get; } = filter.Steep;

    /// <summary>Some modulation moves its cutoff, so its coefficients are worked out as the note plays.</summary>
    public bool Moves { get; } = moves;

    /// <summary>The coefficients when nothing moves the cutoff.</summary>
    public SvfCoefficients Fixed { get; } = SvfCoefficients.For(filter.Cutoff, filter.Resonance);
}

/// <summary>One modulation route worked out once.</summary>
internal readonly record struct ModulationPlan(ModulationTarget Target, double Amount, int Filter, bool IsLfo, double Rate, LfoShape Shape, double Phase, Envelope Envelope);

/// <summary>An instrument's settings worked out once for every voice that plays it.</summary>
internal sealed class InstrumentPlan
{
    public InstrumentPlan(Instrument instrument)
    {
        Waveform = instrument.Waveform;
        IsNoise = instrument.Waveform >= Waveform.WhiteNoise;
        PulseWidth = instrument.PulseWidth;

        var unison = IsNoise ? null : instrument.Unison;
        Unison = unison?.Voices ?? 1;
        UnisonRatio = new double[Unison];
        UnisonLeft = new float[Unison];
        UnisonRight = new float[Unison];
        var normal = 1 / Math.Sqrt(Unison);
        for (var k = 0; k < Unison; k++)
        {
            var position = Unison == 1 ? 0 : k / (double)(Unison - 1) * 2 - 1; // −1 .. 1 across the stack
            UnisonRatio[k] = Math.Pow(2, (unison?.Detune ?? 0) * position / 2 / 12);
            var side = position * (unison?.Spread ?? 0);
            UnisonLeft[k] = (float)(Math.Min(1, 1 - side) * normal);
            UnisonRight[k] = (float)(Math.Min(1, 1 + side) * normal);
        }
        Stereo = Unison > 1 && unison!.Spread > 0;

        Modulations = [.. instrument.Modulations.Select(m => new ModulationPlan(
            m.Target, m.Amount, m.Filter, m.Lfo is not null,
            m.Lfo?.Rate ?? 0, m.Lfo?.Shape ?? LfoShape.Sine, m.Lfo?.Phase ?? 0, m.Envelope ?? default))];
        bool Moved(ModulationTarget target) => Modulations.Any(m => m.Target == target);

        HasFm = !IsNoise && (instrument.Fm is not null || Moved(ModulationTarget.FmIndex));
        FmRatio = instrument.Fm?.Ratio ?? 1;
        FmIndex = instrument.Fm?.Index ?? 0;
        NoiseMix = IsNoise ? 0 : (float)instrument.Noise;
        Envelope = instrument.Envelope;
        Filters = [.. instrument.Filters.Select((f, i) => new FilterPlan(f, Modulations.Any(m => m.Target == ModulationTarget.Cutoff && m.Filter == i)))];
        HasDrive = instrument.Drive > 0;
        DriveGain = (float)(1 + 4 * instrument.Drive);
        DriveNormal = 1 / MathF.Tanh(DriveGain);
        if (instrument.Phaser is { Depth: > 0 } phaser)
        {
            HasPhaser = true;
            PhaserRate = phaser.Rate;
            PhaserMix = (float)(phaser.Depth / 2);
            PhaserCenter = phaser.Center;
            PhaserSweep = phaser.Sweep;
        }

        // Constant power, unity in the middle.
        var angle = (instrument.Pan + 1) * Math.PI / 4;
        PanLeft = (float)(Math.Cos(angle) * Math.Sqrt(2));
        PanRight = (float)(Math.Sin(angle) * Math.Sqrt(2));
        Gain = (float)instrument.Gain;
        Part = (int)instrument.Part;
        var sends = instrument.Sends;
        Dry = (float)(sends?.Dry ?? 1);
        Reverb = (float)(sends?.Reverb ?? 0);
        Delay = (float)(sends?.Delay ?? 0);
        Bus = Reverb > 0 || Delay > 0 ? sends!.Bus : -1;
        DefaultPitch = instrument.DefaultPitch;
    }

    public Waveform Waveform { get; }
    public bool IsNoise { get; }
    public double PulseWidth { get; }
    public int Unison { get; }
    public double[] UnisonRatio { get; }
    public float[] UnisonLeft { get; }
    public float[] UnisonRight { get; }

    /// <summary>The voice's two channels differ before panning (a spread unison), so they are filtered apart.</summary>
    public bool Stereo { get; }

    public ModulationPlan[] Modulations { get; }
    public bool HasFm { get; }
    public double FmRatio { get; }
    public double FmIndex { get; }
    public float NoiseMix { get; }
    public Envelope Envelope { get; }
    public FilterPlan[] Filters { get; }
    public bool HasDrive { get; }
    public float DriveGain { get; }
    public float DriveNormal { get; }
    public bool HasPhaser { get; }
    public double PhaserRate { get; }
    public float PhaserMix { get; }
    public double PhaserCenter { get; }
    public double PhaserSweep { get; }
    public float PanLeft { get; }
    public float PanRight { get; }
    public float Gain { get; }
    public int Part { get; }

    /// <summary>The effect bus it sends to, or −1 for none.</summary>
    public int Bus { get; }

    public float Dry { get; }
    public float Reverb { get; }
    public float Delay { get; }
    public double DefaultPitch { get; }
}

/// <summary>
/// One sounding note. The modulation matrix, and with it pitch, cutoffs, loudness, pulse width, FM index and
/// the phaser, moves at a control rate (every <see cref="ControlFrames"/> samples); oscillators, filters and the
/// amplitude envelope run every sample. Rendering never allocates.
/// </summary>
internal sealed class Voice
{
    private const int ControlFrames = 16;
    private const int PhaserStages = 4;
    private const double MinimumRelease = 0.002;
    private const double MaxIncrement = Dsp.MaxFrequency / Dsp.SampleRate;

    private readonly double[] phases = new double[Song.MaxUnison];
    private readonly EnvelopeState[] modulationEnvelopes = new EnvelopeState[Song.MaxModulations];
    private readonly double[] lfoPhases = new double[Song.MaxModulations];
    private readonly double[] cutoffOctaves = new double[Song.MaxFilters];
    private readonly SvfCoefficients[] coefficients = new SvfCoefficients[Song.MaxFilters];
    // Per filter: first stage left and right, second stage left and right.
    private readonly SvfState[] filterStates = new SvfState[Song.MaxFilters * 4];
    private readonly AllpassState[] allpasses = new AllpassState[PhaserStages * 2];
    private InstrumentPlan plan = null!;
    private EnvelopeState amplitude;
    private NoiseState noise;
    private double frequency, fmPhase, phaserPhase;
    private float level;
    private int startDelay;
    private long gate;

    public bool Active { get; private set; }

    /// <summary>When it started, among all the voices: the oldest makes way first.</summary>
    public long Order { get; private set; }

    public InstrumentPlan Plan => plan;

    public bool Releasing => amplitude.Releasing;

    /// <summary>How loud it is now, for choosing which voice makes way.</summary>
    public double Loudness => amplitude.Level * level;

    /// <summary>
    /// Starts a note <paramref name="delay"/> samples into the next block, holding it <paramref name="gateFrames"/>
    /// samples before its release.
    /// </summary>
    public void Start(InstrumentPlan instrument, double pitch, float velocity, long gateFrames, int delay, ulong seed, long order)
    {
        plan = instrument;
        Active = true;
        Order = order;
        frequency = Dsp.MidiToHz(pitch);
        level = instrument.Gain * velocity;
        startDelay = delay;
        gate = gateFrames;
        noise = new NoiseState(seed);
        for (var k = 0; k < instrument.Unison; k++)
            phases[k] = instrument.Unison == 1 ? 0 : noise.NextUnit();
        fmPhase = phaserPhase = 0;
        amplitude.Start(instrument.Envelope, MinimumRelease);
        var modulations = instrument.Modulations;
        for (var m = 0; m < modulations.Length; m++)
        {
            if (modulations[m].IsLfo) lfoPhases[m] = modulations[m].Phase;
            else modulationEnvelopes[m].Start(modulations[m].Envelope, 0);
        }
        for (var f = 0; f < instrument.Filters.Length; f++)
            coefficients[f] = instrument.Filters[f].Fixed;
        for (var i = 0; i < instrument.Filters.Length * 4; i++)
            filterStates[i].Reset();
        for (var i = 0; i < allpasses.Length; i++)
            allpasses[i].Reset();
    }

    /// <summary>Silences it at once (its slot is wanted, or playback starts over).</summary>
    public void Stop() => Active = false;

    /// <summary>Overwrites the two channels with its next samples; false once it has ended (the rest is silence).</summary>
    public bool Render(Span<float> left, Span<float> right)
    {
        var frames = left.Length;
        var start = 0;
        if (startDelay > 0)
        {
            start = Math.Min(startDelay, frames);
            left[..start].Clear();
            right[..start].Clear();
            startDelay -= start;
        }
        var p = plan;
        var filters = p.Filters;
        for (var s = start; s < frames; s += ControlFrames)
        {
            var count = Math.Min(ControlFrames, frames - s);

            // The modulation matrix: every route adds to (or, for loudness, scales) its target.
            double semitones = 0, widthOffset = 0, fmIndex = p.FmIndex, loudness = 1;
            Array.Clear(cutoffOctaves);
            var modulations = p.Modulations;
            for (var m = 0; m < modulations.Length; m++)
            {
                ref readonly var route = ref modulations[m];
                double value, unipolar;
                if (route.IsLfo)
                {
                    value = LfoValue(route.Shape, lfoPhases[m]);
                    unipolar = 0.5 + 0.5 * value;
                    lfoPhases[m] = Dsp.Wrap(lfoPhases[m] + route.Rate * count / Dsp.SampleRate);
                }
                else unipolar = value = modulationEnvelopes[m].Advance(count);
                switch (route.Target)
                {
                    case ModulationTarget.Pitch: semitones += route.Amount * value; break;
                    case ModulationTarget.Cutoff: cutoffOctaves[route.Filter] += route.Amount * value; break;
                    case ModulationTarget.Amplitude: loudness *= 1 - route.Amount * (1 - unipolar); break;
                    case ModulationTarget.PulseWidth: widthOffset += route.Amount * value; break;
                    case ModulationTarget.FmIndex: fmIndex += route.Amount * value; break;
                }
            }

            var increment = (semitones == 0 ? frequency : frequency * Math.Pow(2, semitones / 12)) / Dsp.SampleRate;
            double modulatorIncrement = 0, deviation = 0;
            if (p.HasFm)
            {
                modulatorIncrement = increment * p.FmRatio;
                deviation = Math.Max(0, fmIndex) * modulatorIncrement;
            }
            for (var f = 0; f < filters.Length; f++)
                if (filters[f].Moves)
                    coefficients[f] = SvfCoefficients.For(filters[f].Cutoff * Math.Pow(2, cutoffOctaves[f]), filters[f].Resonance);
            var gain = level * (float)Math.Max(0, loudness);
            var width = widthOffset == 0 ? p.PulseWidth : Math.Clamp(p.PulseWidth + widthOffset, 0.02, 0.98);
            var allpass = 0f;
            if (p.HasPhaser)
            {
                allpass = AllpassState.Coefficient(p.PhaserCenter + p.PhaserSweep * SineTable.Sin(phaserPhase));
                phaserPhase = Dsp.Wrap(phaserPhase + p.PhaserRate * count / Dsp.SampleRate);
            }

            for (var i = s; i < s + count; i++)
            {
                if (gate-- == 0) Release();

                float l, r = 0;
                if (p.IsNoise)
                {
                    l = p.Waveform switch
                    {
                        Waveform.WhiteNoise => noise.White(),
                        Waveform.PinkNoise => noise.Pink(),
                        _ => noise.Brown(),
                    };
                }
                else
                {
                    var step = increment;
                    if (p.HasFm)
                    {
                        step += deviation * SineTable.Sin(fmPhase);
                        fmPhase = Dsp.Wrap(fmPhase + modulatorIncrement);
                    }
                    step = Math.Clamp(step, -MaxIncrement, MaxIncrement);
                    if (p.Unison == 1)
                    {
                        l = Oscillator.Sample(p.Waveform, phases[0], step, width);
                        phases[0] = Dsp.Wrap(phases[0] + step);
                    }
                    else
                    {
                        l = 0;
                        for (var k = 0; k < p.Unison; k++)
                        {
                            var unisonStep = step * p.UnisonRatio[k];
                            var x = Oscillator.Sample(p.Waveform, phases[k], unisonStep, width);
                            phases[k] = Dsp.Wrap(phases[k] + unisonStep);
                            l += x * p.UnisonLeft[k];
                            r += x * p.UnisonRight[k];
                        }
                        if (!p.Stereo) l = (l + r) / 2;
                    }
                    if (p.NoiseMix > 0)
                    {
                        var w = noise.White() * p.NoiseMix;
                        l += w;
                        r += w;
                    }
                }

                for (var f = 0; f < filters.Length; f++)
                {
                    var filter = filters[f];
                    ref readonly var c = ref coefficients[f];
                    var states = 4 * f;
                    l = filterStates[states].Process(l, c, filter.Mode);
                    if (filter.Steep) l = filterStates[states + 2].Process(l, c, filter.Mode);
                    if (p.Stereo)
                    {
                        r = filterStates[states + 1].Process(r, c, filter.Mode);
                        if (filter.Steep) r = filterStates[states + 3].Process(r, c, filter.Mode);
                    }
                }
                if (!p.Stereo) r = l;

                if (p.HasDrive)
                {
                    l = MathF.Tanh(l * p.DriveGain) * p.DriveNormal;
                    r = p.Stereo ? MathF.Tanh(r * p.DriveGain) * p.DriveNormal : l;
                }

                if (p.HasPhaser)
                {
                    var dryShare = 1 - p.PhaserMix;
                    var wl = l;
                    for (var a = 0; a < PhaserStages; a++)
                        wl = allpasses[a].Process(wl, allpass);
                    l = l * dryShare + wl * p.PhaserMix;
                    if (p.Stereo)
                    {
                        var wr = r;
                        for (var a = 0; a < PhaserStages; a++)
                            wr = allpasses[PhaserStages + a].Process(wr, allpass);
                        r = r * dryShare + wr * p.PhaserMix;
                    }
                    else r = l;
                }

                var envelope = (float)amplitude.Next() * gain;
                left[i] = l * envelope * p.PanLeft;
                right[i] = r * envelope * p.PanRight;
            }

            if (amplitude.Done)
            {
                left[(s + count)..].Clear();
                right[(s + count)..].Clear();
                Active = false;
                return false;
            }
        }
        return true;
    }

    /// <summary>An LFO's value, −1 to 1, at <paramref name="phase"/>; every shape starts at its zero crossing but the saw, which starts at −1.</summary>
    private static double LfoValue(LfoShape shape, double phase) => shape switch
    {
        LfoShape.Sine => SineTable.Sin(phase),
        LfoShape.Triangle => phase < 0.25 ? 4 * phase : phase < 0.75 ? 2 - 4 * phase : 4 * phase - 4,
        LfoShape.Square => phase < 0.5 ? 1 : -1,
        _ => 2 * phase - 1,
    };

    private void Release()
    {
        amplitude.Release();
        for (var m = 0; m < plan.Modulations.Length; m++)
            if (!plan.Modulations[m].IsLfo) modulationEnvelopes[m].Release();
    }
}
