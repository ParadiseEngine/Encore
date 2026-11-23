namespace Encore.Audio.Music.Synth;

/// <summary>
/// A feedback delay network reverb: eight delay lines of mutually prime lengths, each darkened by a one-pole
/// lowpass and scaled so it loses 60 dB in the bus's reverb time, mixed back into one another through a
/// Householder reflection (lossless, so the decay is the gains' alone). Even lines feed and form the left
/// channel, odd lines the right.
/// </summary>
internal sealed class FdnReverb
{
    private const int Lines = 8;
    private const float InputGain = 0.5f;
    private const float OutputGain = 0.35f;

    private readonly float[][] lines = new float[Lines][];
    private readonly int[] positions = new int[Lines];
    private readonly float[] feedback = new float[Lines];
    private readonly float[] damped = new float[Lines];
    private readonly float[] returns = new float[Lines];
    private readonly float damping;

    public FdnReverb(EffectBus bus)
    {
        ReadOnlySpan<int> baseLengths = [1031, 1171, 1301, 1433, 1559, 1693, 1811, 1949];
        var scale = 0.6 + 1.4 * bus.ReverbSize;
        for (var i = 0; i < Lines; i++)
        {
            var length = Math.Max(1, (int)Math.Round(baseLengths[i] * scale));
            lines[i] = new float[length];
            feedback[i] = (float)Math.Pow(10, -3.0 * length / (bus.ReverbSeconds * Dsp.SampleRate));
        }
        damping = Dsp.OnePole(bus.ReverbDamping);
    }

    public void Reset()
    {
        foreach (var line in lines) Array.Clear(line);
        Array.Clear(positions);
        Array.Clear(damped);
    }

    /// <summary>Adds the reverb of <paramref name="inLeft"/> and <paramref name="inRight"/> to <paramref name="left"/> and <paramref name="right"/>.</summary>
    public void Process(ReadOnlySpan<float> inLeft, ReadOnlySpan<float> inRight, Span<float> left, Span<float> right)
    {
        for (var n = 0; n < inLeft.Length; n++)
        {
            float sum = 0, outLeft = 0, outRight = 0;
            for (var i = 0; i < Lines; i++)
            {
                var y = lines[i][positions[i]];
                if ((i & 1) == 0) outLeft += y;
                else outRight += y;
                var d = damped[i] = y + (damped[i] - y) * damping;
                var v = returns[i] = d * feedback[i];
                sum += v;
            }
            var reflection = sum * (2f / Lines);
            var xl = inLeft[n] * InputGain;
            var xr = inRight[n] * InputGain;
            for (var i = 0; i < Lines; i++)
            {
                var line = lines[i];
                line[positions[i]] = Dsp.Flush(returns[i] - reflection + ((i & 1) == 0 ? xl : xr));
                if (++positions[i] == line.Length) positions[i] = 0;
            }
            left[n] += outLeft * OutputGain;
            right[n] += outRight * OutputGain;
        }
    }
}

/// <summary>A stereo feedback delay whose echoes darken through a one-pole lowpass.</summary>
internal sealed class StereoDelay
{
    private readonly float[] left;
    private readonly float[] right;
    private readonly double delay;
    private readonly float feedback;
    private readonly float damping;
    private int position;
    private float dampedLeft, dampedRight;

    public StereoDelay(EffectBus bus)
    {
        delay = Math.Max(1, bus.DelaySeconds * Dsp.SampleRate);
        var length = (int)Math.Ceiling(delay) + 2;
        left = new float[length];
        right = new float[length];
        feedback = (float)bus.DelayFeedback;
        damping = Dsp.OnePole(bus.DelayDamping);
    }

    public void Reset()
    {
        Array.Clear(left);
        Array.Clear(right);
        position = 0;
        dampedLeft = dampedRight = 0;
    }

    /// <summary>Adds the echoes of <paramref name="inLeft"/> and <paramref name="inRight"/> to <paramref name="outLeft"/> and <paramref name="outRight"/>.</summary>
    public void Process(ReadOnlySpan<float> inLeft, ReadOnlySpan<float> inRight, Span<float> outLeft, Span<float> outRight)
    {
        var length = left.Length;
        for (var n = 0; n < inLeft.Length; n++)
        {
            var read = position - delay;
            if (read < 0) read += length;
            var i0 = (int)read;
            var fraction = (float)(read - i0);
            var i1 = i0 + 1 == length ? 0 : i0 + 1;
            var yl = left[i0] + (left[i1] - left[i0]) * fraction;
            var yr = right[i0] + (right[i1] - right[i0]) * fraction;
            dampedLeft = yl + (dampedLeft - yl) * damping;
            dampedRight = yr + (dampedRight - yr) * damping;
            left[position] = Dsp.Flush(inLeft[n] + dampedLeft * feedback);
            right[position] = Dsp.Flush(inRight[n] + dampedRight * feedback);
            outLeft[n] += yl;
            outRight[n] += yr;
            if (++position == length) position = 0;
        }
    }
}

/// <summary>One of the song's effect buses as it plays: what the voices sent this block, and the effects it feeds.</summary>
internal sealed class BusState(EffectBus bus, bool hasReverb, bool hasDelay)
{
    public readonly float[] ReverbLeft = new float[Sequencer.Block];
    public readonly float[] ReverbRight = new float[Sequencer.Block];
    public readonly float[] DelayLeft = new float[Sequencer.Block];
    public readonly float[] DelayRight = new float[Sequencer.Block];
    private readonly FdnReverb? reverb = hasReverb ? new FdnReverb(bus) : null;
    private readonly StereoDelay? delay = hasDelay ? new StereoDelay(bus) : null;

    public void Clear()
    {
        Array.Clear(ReverbLeft);
        Array.Clear(ReverbRight);
        Array.Clear(DelayLeft);
        Array.Clear(DelayRight);
    }

    public void Reset()
    {
        Clear();
        reverb?.Reset();
        delay?.Reset();
    }

    /// <summary>Adds the bus's effects of this block's sends to the output.</summary>
    public void Process(Span<float> left, Span<float> right)
    {
        reverb?.Process(ReverbLeft, ReverbRight, left, right);
        delay?.Process(DelayLeft, DelayRight, left, right);
    }
}
