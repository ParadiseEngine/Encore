using Encore.Audio.Music;
using Encore.Audio.Music.Synth;

namespace Encore.Tests;

/// <summary>The synthesizer's building blocks against what their textbook definitions predict.</summary>
public sealed class SynthDspTests
{
    private const int Rate = Dsp.SampleRate;

    /// <summary>The RMS gain of a filter for a sine at <paramref name="frequency"/>, after it settles.</summary>
    private static double FilterGain(FilterMode mode, double cutoff, double q, double frequency, bool steep = false)
    {
        var coefficients = SvfCoefficients.For(cutoff, q);
        SvfState first = default, second = default;
        double inSum = 0, outSum = 0;
        for (var n = 0; n < Rate; n++)
        {
            var x = (float)Math.Sin(2 * Math.PI * frequency * n / Rate);
            var y = first.Process(x, coefficients, mode);
            if (steep) y = second.Process(y, coefficients, mode);
            if (n < Rate / 2) continue;
            inSum += x * x;
            outSum += y * y;
        }
        return Math.Sqrt(outSum / inSum);
    }

    [Test]
    public async Task TheLowpassPassesTheBassAndCutsTheTreble()
    {
        await Assert.That(FilterGain(FilterMode.Lowpass, 1000, 0.7071, 100)).IsGreaterThan(0.99).And.IsLessThan(1.01);
        // −3 dB at the cutoff for a Butterworth Q, about −12 dB an octave above.
        await Assert.That(FilterGain(FilterMode.Lowpass, 1000, 0.7071, 1000)).IsGreaterThan(0.69).And.IsLessThan(0.73);
        await Assert.That(FilterGain(FilterMode.Lowpass, 1000, 0.7071, 8000)).IsLessThan(0.02);
        // Two stages cut twice as steeply.
        await Assert.That(FilterGain(FilterMode.Lowpass, 1000, 0.7071, 8000, steep: true)).IsLessThan(0.0004);
        // Resonance rings at the cutoff: a gain of about Q there.
        await Assert.That(FilterGain(FilterMode.Lowpass, 1000, 4, 1000)).IsGreaterThan(3.8).And.IsLessThan(4.2);
    }

    [Test]
    public async Task TheHighpassAndBandpassKeepTheirBands()
    {
        await Assert.That(FilterGain(FilterMode.Highpass, 1000, 0.7071, 8000)).IsGreaterThan(0.98);
        await Assert.That(FilterGain(FilterMode.Highpass, 1000, 0.7071, 100)).IsLessThan(0.02);
        // The bandpass peaks at unity whatever its Q, and narrows as Q grows.
        await Assert.That(FilterGain(FilterMode.Bandpass, 1000, 5, 1000)).IsGreaterThan(0.99).And.IsLessThan(1.01);
        await Assert.That(FilterGain(FilterMode.Bandpass, 1000, 5, 2000)).IsLessThan(FilterGain(FilterMode.Bandpass, 1000, 1, 2000));
    }

    [Test]
    public async Task TheEnvelopeRisesDecaysHoldsAndReleasesInItsTimes()
    {
        var envelope = new EnvelopeState();
        envelope.Start(new Envelope(0.01, 0.1, 0.5, 0.2), 0);
        var levels = new double[Rate];
        for (var n = 0; n < 24_000; n++) levels[n] = envelope.Next();
        await Assert.That(levels[239]).IsGreaterThan(0.49).And.IsLessThan(0.51); // half way through the attack's 480 frames
        await Assert.That(levels[470]).IsLessThan(1.0);
        await Assert.That(levels.Take(490).Max()).IsEqualTo(1.0); // the peak, as the attack ends
        await Assert.That(levels[2_000]).IsGreaterThan(0.5).And.IsLessThan(1.0); // decaying
        await Assert.That(levels[480 + 4_800 + 10]).IsEqualTo(0.5); // sustained after 100 ms of decay
        await Assert.That(levels[23_999]).IsEqualTo(0.5);
        envelope.Release();
        for (var n = 24_000; n < Rate; n++) levels[n] = envelope.Next();
        await Assert.That(levels[24_000 + 4_800]).IsLessThan(0.1).And.IsGreaterThan(0.0); // falling fast
        await Assert.That(levels[24_000 + 9_600 + 1]).IsEqualTo(0.0); // gone after 200 ms
        await Assert.That(envelope.Done).IsTrue();
    }

    [Test]
    public async Task AnEnvelopeReleasedDuringItsAttackFallsFromWhereItWas()
    {
        var envelope = new EnvelopeState();
        envelope.Start(new Envelope(0.1, 0, 1, 0.01), 0);
        double level = 0;
        for (var n = 0; n < 2_400; n++) level = envelope.Next();
        await Assert.That(level).IsGreaterThan(0.49).And.IsLessThan(0.51);
        envelope.Release();
        var next = envelope.Next();
        await Assert.That(next).IsLessThan(level).And.IsGreaterThan(level * 0.9);
    }

    [Test]
    public async Task APercussiveEnvelopeEndsWithoutARelease()
    {
        var envelope = new EnvelopeState();
        envelope.Start(Envelope.Hit(0.05), 0);
        var frames = 0;
        while (!envelope.Done && frames < Rate)
        {
            envelope.Next();
            frames++;
        }
        await Assert.That(frames).IsGreaterThan(2_400).And.IsLessThan(2_500);
    }

    [Test]
    [Arguments(Waveform.Sine)]
    [Arguments(Waveform.Triangle)]
    [Arguments(Waveform.Sawtooth)]
    [Arguments(Waveform.Square)]
    [Arguments(Waveform.Pulse)]
    public async Task OscillatorsRepeatAtTheirFrequencyWithinFullScale(Waveform waveform)
    {
        const double frequency = 440;
        var increment = frequency / Rate;
        double phase = 0, mean = 0;
        var peak = 0f;
        var upward = 0;
        var previous = Oscillator.Sample(waveform, 0, increment, 0.3);
        for (var n = 1; n < Rate; n++)
        {
            phase = Dsp.Wrap(phase + increment);
            var x = Oscillator.Sample(waveform, phase, increment, 0.3);
            peak = Math.Max(peak, MathF.Abs(x));
            mean += x;
            if (previous < 0 && x >= 0) upward++;
            previous = x;
        }
        await Assert.That(peak).IsLessThanOrEqualTo(1.9f);
        await Assert.That(Math.Abs(mean / Rate)).IsLessThan(0.01); // no DC, the pulse's removed too
        await Assert.That(upward).IsGreaterThanOrEqualTo(439).And.IsLessThanOrEqualTo(441);
    }

    [Test]
    public async Task TheBandLimitedSawIsSmootherAtItsJumpThanANaiveOne()
    {
        // At 5 kHz a naive saw jumps by 2 in one sample; the corrected one spreads the jump over two.
        const double increment = 5000.0 / Rate;
        var largest = 0f;
        double phase = 0;
        var previous = Oscillator.Sample(Waveform.Sawtooth, phase, increment, 0.5);
        for (var n = 0; n < 1000; n++)
        {
            phase = Dsp.Wrap(phase + increment);
            var x = Oscillator.Sample(Waveform.Sawtooth, phase, increment, 0.5);
            largest = Math.Max(largest, MathF.Abs(x - previous));
            previous = x;
        }
        await Assert.That(largest).IsLessThan(1.6f);
    }

    [Test]
    public async Task NoisesAreBoundedAndReplayTheirSeed()
    {
        var a = new NoiseState(42);
        var b = new NoiseState(42);
        var c = new NoiseState(43);
        var colored = new NoiseState(44);
        var same = true;
        var differs = false;
        var peak = 0f;
        for (var n = 0; n < Rate; n++)
        {
            var white = a.White();
            same &= white == b.White();
            differs |= white != c.White();
            peak = Math.Max(peak, Math.Max(MathF.Abs(colored.Pink()), MathF.Abs(colored.Brown())));
        }
        await Assert.That(same).IsTrue();
        await Assert.That(differs).IsTrue();
        await Assert.That(peak).IsLessThan(3f);
    }

    [Test]
    public async Task TheBrownNoiseIsDarkerThanThePinkWhichIsDarkerThanTheWhite()
    {
        // The mean squared difference between neighbouring samples measures the energy up high.
        static double Roughness(Func<float> next)
        {
            double sum = 0, energy = 0;
            var previous = next();
            for (var n = 0; n < Rate; n++)
            {
                var x = next();
                sum += (x - previous) * (x - previous);
                energy += x * x;
                previous = x;
            }
            return sum / energy;
        }
        var noise = new NoiseState(7);
        var white = Roughness(() => noise.White());
        var pink = Roughness(() => noise.Pink());
        var brown = Roughness(() => noise.Brown());
        await Assert.That(pink).IsLessThan(white);
        await Assert.That(brown).IsLessThan(pink);
    }

    [Test]
    public async Task TheReverbDecaysByItsTimeAndStaysFinite()
    {
        var reverb = new FdnReverb(new EffectBus { ReverbSeconds = 1, ReverbSize = 0.5, ReverbDamping = 24_000 });
        var block = Sequencer.Block;
        var inLeft = new float[block];
        var inRight = new float[block];
        var left = new float[block];
        var right = new float[block];
        double early = 0, late = 0;
        for (var b = 0; b < Rate * 2 / block; b++)
        {
            Array.Clear(inLeft);
            Array.Clear(inRight);
            if (b == 0) inLeft[0] = inRight[0] = 1;
            Array.Clear(left);
            Array.Clear(right);
            reverb.Process(inLeft, inRight, left, right);
            var t = b * block;
            for (var i = 0; i < block; i++)
            {
                var energy = left[i] * left[i] + right[i] * right[i];
                if (t + i is >= 4_800 and < 9_600) early += energy; // 0.1 to 0.2 s
                if (t + i is >= Rate + 4_800 and < Rate + 9_600) late += energy; // a second later
            }
        }
        var drop = 10 * Math.Log10(early / late);
        await Assert.That(double.IsFinite(drop)).IsTrue();
        // Undamped, a second at a one-second reverb time is 60 dB down, give or take the network's spread.
        await Assert.That(drop).IsGreaterThan(50).And.IsLessThan(70);
    }

    [Test]
    public async Task TheDelayEchoesAfterItsTimeAndFeedsBack()
    {
        var delay = new StereoDelay(new EffectBus { DelaySeconds = 0.01, DelayFeedback = 0.5, DelayDamping = 24_000 });
        var length = 2_000;
        var input = new float[length];
        input[0] = 1;
        var silence = new float[length];
        var left = new float[length];
        var right = new float[length];
        delay.Process(input, silence, left, right);
        var first = Array.IndexOf(left, left.Max());
        await Assert.That(first).IsEqualTo(480);
        await Assert.That(left[480]).IsGreaterThan(0.99f);
        await Assert.That(left[960]).IsGreaterThan(0.45f).And.IsLessThan(0.55f); // the echo of the echo
        await Assert.That(right.All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task TheSineTableMatchesTheSine()
    {
        var worst = 0.0;
        for (var i = 0; i < 10_000; i++)
        {
            var phase = i / 10_000.0;
            worst = Math.Max(worst, Math.Abs(SineTable.Sin(phase) - Math.Sin(2 * Math.PI * phase)));
        }
        await Assert.That(worst).IsLessThan(1e-6);
        await Assert.That(SineTable.Sin(1.0)).IsEqualTo(SineTable.Sin(0.0));
    }

    [Test]
    public async Task TheGeneratorReplaysItsSeed()
    {
        var a = new Rng(5);
        var b = new Rng(5);
        for (var i = 0; i < 1000; i++)
        {
            var unit = a.NextUnit();
            await Assert.That(unit).IsEqualTo(b.NextUnit());
            if (unit is < 0 or >= 1) throw new InvalidOperationException("NextUnit left [0, 1).");
        }
        await Assert.That(Rng.Seed(1, 2, 3)).IsNotEqualTo(Rng.Seed(1, 2, 4));
    }
}
