namespace Encore.Audio.Music.Synth;

/// <summary>
/// Plays a <see cref="Song"/> block by block: starts each note on its sample (again every repetition when
/// looping), renders the voices, scales each by its part's arranged gain, and sums them dry and into their
/// effect buses. Voices, buses and arrangement carry across the loop seam. Rendering never allocates.
/// </summary>
internal sealed class Sequencer
{
    /// <summary>Frames a block renders: the step at which the arrangement moves and notes are scheduled.</summary>
    public const int Block = 64;

    /// <summary>Voices sounding at once; past it the quietest releasing voice (or else the oldest) makes way.</summary>
    public const int MaxVoices = 128;

    private readonly InstrumentPlan[] plans;
    private readonly long[] starts;
    private readonly long[] gates;
    private readonly double[] pitches;
    private readonly float[] velocities;
    private readonly int[] instruments;
    private readonly long loopFrames;
    private readonly ulong seed;
    private readonly Voice[] voices = new Voice[MaxVoices];
    private readonly BusState?[] buses;
    private readonly float[] voiceLeft = new float[Block];
    private readonly float[] voiceRight = new float[Block];
    private readonly float[] partGain = new float[ScoreParts.Count];
    private readonly float[] partTarget = new float[ScoreParts.Count];
    private readonly float[] partStep = new float[ScoreParts.Count];
    private readonly float[] partStart = new float[ScoreParts.Count];
    private long frame, repetitionStart, order;
    private int nextNote, repetition;
    private bool loop;

    public Sequencer(Song song, long loopFrames)
    {
        this.loopFrames = loopFrames;
        seed = song.Seed;
        plans = [.. song.Instruments.Select(i => new InstrumentPlan(i))];
        var notes = song.Notes;
        starts = new long[notes.Count];
        gates = new long[notes.Count];
        pitches = new double[notes.Count];
        velocities = new float[notes.Count];
        instruments = new int[notes.Count];
        for (var n = 0; n < notes.Count; n++)
        {
            var note = notes[n];
            starts[n] = Math.Min((long)(note.Start * Dsp.SampleRate), loopFrames - 1);
            gates[n] = (long)Math.Round(note.Duration * Dsp.SampleRate);
            pitches[n] = note.Pitch ?? song.Instruments[note.Instrument].DefaultPitch;
            velocities[n] = note.Velocity;
            instruments[n] = note.Instrument;
        }

        buses = new BusState?[song.Effects.Count];
        for (var b = 0; b < buses.Length; b++)
        {
            bool reverb = false, delay = false;
            foreach (var plan in plans)
            {
                if (plan.Bus != b) continue;
                reverb |= plan.Reverb > 0;
                delay |= plan.Delay > 0;
            }
            if (reverb || delay) buses[b] = new BusState(song.Effects[b], reverb, delay);
        }

        for (var v = 0; v < voices.Length; v++) voices[v] = new Voice();
        Array.Fill(partGain, 1f);
        Array.Fill(partTarget, 1f);
    }

    public int ActiveVoices
    {
        get
        {
            var count = 0;
            foreach (var voice in voices)
                if (voice.Active) count++;
            return count;
        }
    }

    public float PartGain(ScorePart part) => partGain[(int)part];

    /// <summary>Moves each part's gain to <paramref name="gains"/> in a straight line over <paramref name="seconds"/> (0: at once).</summary>
    public void Arrange(ReadOnlySpan<float> gains, double seconds)
    {
        for (var p = 0; p < partGain.Length; p++)
        {
            partTarget[p] = gains[p];
            if (seconds <= 0)
            {
                partGain[p] = gains[p];
                partStep[p] = 0;
            }
            else partStep[p] = (float)(Math.Abs(gains[p] - partGain[p]) / (seconds * Dsp.SampleRate));
        }
    }

    /// <summary>Back to the beginning in silence; the arrangement stays.</summary>
    public void Reset(bool loop)
    {
        this.loop = loop;
        foreach (var voice in voices) voice.Stop();
        foreach (var bus in buses) bus?.Reset();
        frame = repetitionStart = order = 0;
        nextNote = repetition = 0;
    }

    /// <summary>Overwrites <see cref="Block"/> frames of each channel.</summary>
    public void Render(Span<float> left, Span<float> right)
    {
        left.Clear();
        right.Clear();
        foreach (var bus in buses) bus?.Clear();

        var end = frame + Block;
        if (starts.Length > 0)
        {
            while (true)
            {
                if (nextNote == starts.Length)
                {
                    if (!loop) break;
                    nextNote = 0;
                    repetition++;
                    repetitionStart += loopFrames;
                }
                var at = repetitionStart + starts[nextNote];
                if (at >= end) break;
                Trigger(nextNote, (int)(at - frame));
                nextNote++;
            }
        }

        for (var p = 0; p < partGain.Length; p++)
        {
            partStart[p] = partGain[p];
            var move = partStep[p] * Block;
            partGain[p] = partGain[p] < partTarget[p]
                ? Math.Min(partTarget[p], partGain[p] + move)
                : Math.Max(partTarget[p], partGain[p] - move);
        }

        foreach (var voice in voices)
        {
            if (!voice.Active) continue;
            voice.Render(voiceLeft, voiceRight);
            var plan = voice.Plan;
            float from = partStart[plan.Part], to = partGain[plan.Part];
            if (from == 0 && to == 0) continue;
            var slope = (to - from) / Block;
            var bus = plan.Bus >= 0 ? buses[plan.Bus] : null;
            for (var i = 0; i < Block; i++)
            {
                var g = from + slope * (i + 1);
                var l = voiceLeft[i] * g;
                var r = voiceRight[i] * g;
                left[i] += l * plan.Dry;
                right[i] += r * plan.Dry;
                if (bus is null) continue;
                bus.ReverbLeft[i] += l * plan.Reverb;
                bus.ReverbRight[i] += r * plan.Reverb;
                bus.DelayLeft[i] += l * plan.Delay;
                bus.DelayRight[i] += r * plan.Delay;
            }
        }

        foreach (var bus in buses) bus?.Process(left, right);
        frame = end;
    }

    private void Trigger(int note, int offset)
    {
        voices[FreeVoice()].Start(plans[instruments[note]], pitches[note], velocities[note], gates[note], offset,
            Rng.Seed(seed, (ulong)note, (ulong)repetition), order++);
    }

    private int FreeVoice()
    {
        int quietest = -1, oldest = 0;
        for (var v = 0; v < voices.Length; v++)
        {
            var voice = voices[v];
            if (!voice.Active) return v;
            if (voice.Releasing && (quietest < 0 || voice.Loudness < voices[quietest].Loudness)) quietest = v;
            if (voice.Order < voices[oldest].Order) oldest = v;
        }
        return quietest >= 0 ? quietest : oldest;
    }
}
