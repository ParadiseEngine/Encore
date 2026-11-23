using Encore.Audio;
using Encore.Audio.Music;

namespace Encore.Tests;

/// <summary>MusicPlayer's transport, looping and arrangement over songs built in code; no device or bundled assets.</summary>
public sealed class MusicPlayerTests
{
    private const int Rate = MusicPlayer.SampleRate;

    /// <summary>Two plain notes, at the start and half way.</summary>
    internal static Song Simple(double length = 1) => new(
        [new Instrument { Waveform = Waveform.Sine, Gain = 0.5, Envelope = new Envelope(0.01, 0, 1, 0.05) }],
        [new Note(0, length / 4, 60, 1, 0), new Note(length / 2, length / 4, 64, 1, 0)],
        length);

    /// <summary>Every feature the synthesizer has, with notes ringing across the loop seam.</summary>
    internal static Song Rich(double length = 1)
    {
        static Modulation Env(ModulationTarget target, double amount, Envelope envelope, int filter = 0) =>
            Modulation.ByEnvelope(target, amount, envelope) with { Filter = filter };
        static Modulation Wobble(ModulationTarget target, double amount, double rate, LfoShape shape = LfoShape.Sine, int filter = 0) =>
            Modulation.ByLfo(target, amount, rate, shape) with { Filter = filter };

        Instrument[] instruments =
        [
            new() { Part = ScorePart.Kick, Waveform = Waveform.Sine, Gain = 0.6, DefaultPitch = 33, Envelope = Envelope.Hit(0.25), Modulations = [Env(ModulationTarget.Pitch, 24, Envelope.Hit(0.04))] },
            new() { Part = ScorePart.Snare, Waveform = Waveform.Triangle, Noise = 0.6, Gain = 0.3, Envelope = Envelope.Hit(0.15), Sends = new Sends(0) { Reverb = 0.3 }, Filters = [new Filter(FilterMode.Bandpass, 1500) { Resonance = 2 }] },
            new() { Part = ScorePart.Hats, Waveform = Waveform.WhiteNoise, Gain = 0.1, Envelope = Envelope.Hit(0.05), Filters = [new Filter(FilterMode.Highpass, 7000)], Pan = 0.4 },
            new() { Part = ScorePart.Hats, Waveform = Waveform.PinkNoise, Gain = 0.1, Envelope = Envelope.Hit(0.1), Modulations = [Wobble(ModulationTarget.Amplitude, 0.8, 30, LfoShape.Square)] },
            new() { Part = ScorePart.Hats, Waveform = Waveform.BrownNoise, Gain = 0.1, Envelope = Envelope.Hit(0.1) },
            new()
            {
                Part = ScorePart.Bass, Waveform = Waveform.Sawtooth, Gain = 0.3, Drive = 0.5, Envelope = new Envelope(0.003, 0.2, 0.5, 0.1),
                Filters = [new Filter(FilterMode.Lowpass, 400) { Resonance = 3, Steep = true }],
                Modulations = [Env(ModulationTarget.Cutoff, 2, new Envelope(0.002, 0.15, 0, 0.1))],
            },
            new()
            {
                Part = ScorePart.Keys, Waveform = Waveform.Sine, Gain = 0.2, Fm = new Fm(2, 0.3),
                Modulations = [Env(ModulationTarget.FmIndex, 1.5, new Envelope(0.001, 0.3, 0.2, 0.1))],
                Envelope = new Envelope(0.002, 0.4, 0.3, 0.3), Sends = new Sends(1) { Delay = 0.3, Reverb = 0.2 }, Pan = -0.5,
            },
            new()
            {
                Part = ScorePart.Pad, Waveform = Waveform.Sawtooth, Unison = new Unison(5, 0.2, 0.8), Gain = 0.08,
                Envelope = new Envelope(0.05, 0.2, 0.8, 0.4),
                Filters = [new Filter(FilterMode.Lowpass, 1500), new Filter(FilterMode.Highpass, 120)],
                Modulations = [Wobble(ModulationTarget.Cutoff, 1, 0.5, LfoShape.Triangle), Wobble(ModulationTarget.Cutoff, 0.5, 0.3, LfoShape.Saw, filter: 1), Wobble(ModulationTarget.Amplitude, 0.3, 5)],
                Phaser = new Phaser(0.3, 0.6, 1000, 700), Sends = new Sends(0) { Reverb = 0.4 },
            },
            new()
            {
                Part = ScorePart.Melody, Waveform = Waveform.Pulse, PulseWidth = 0.3, Gain = 0.15, Envelope = new Envelope(0.02, 0.1, 0.8, 0.2),
                Modulations = [Wobble(ModulationTarget.PulseWidth, 0.2, 2), Wobble(ModulationTarget.Pitch, 0.2, 5)], Sends = new Sends(0) { Delay = 0.2 },
            },
            new() { Part = ScorePart.Melody, Waveform = Waveform.Square, Gain = 0.1, Envelope = Envelope.Held, Sends = new Sends(1) { Dry = 0.5, Reverb = 0.5 } },
        ];
        EffectBus[] effects = [new() { DelaySeconds = 0.12, DelayFeedback = 0.6 }, new() { ReverbSeconds = 3, ReverbSize = 1, DelaySeconds = 0.3 }];
        var notes = new List<Note>();
        for (var i = 0; i < instruments.Length; i++)
        {
            notes.Add(new Note(i * length / instruments.Length, length * 0.3, 48 + i * 3, 1, i));
            notes.Add(new Note(length * 0.95, length * 0.2, 55 + i, 0.8f, i)); // rings across the seam
        }
        return new Song(instruments, notes, length, effects, seed: 1234);
    }

    private static float[] Render(MusicPlayer player, int frames)
    {
        var buffer = new float[frames * 2];
        player.Render(buffer);
        return buffer;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples) sum += s * s;
        return samples.IsEmpty ? 0 : Math.Sqrt(sum / samples.Length);
    }

    [Test]
    public async Task StartsStoppedAndRendersFiniteStereoWhenPlaying()
    {
        var player = new MusicPlayer(Rich());
        await Assert.That(player.State).IsEqualTo(MusicPlaybackState.Stopped);
        var silent = Render(player, 1000);
        await Assert.That(silent.All(s => s == 0)).IsTrue();

        player.Play();
        var output = Render(player, Rate);
        await Assert.That(output.All(float.IsFinite)).IsTrue();
        await Assert.That(Rms(output)).IsGreaterThan(0.005);
        await Assert.That(output.Max(s => MathF.Abs(s))).IsLessThan(4f);
        // The two channels differ: panned and spread instruments.
        var different = false;
        for (var i = 0; i < output.Length; i += 2) different |= output[i] != output[i + 1];
        await Assert.That(different).IsTrue();
    }

    [Test]
    public async Task TheSameSongRendersTheSameSamples()
    {
        var a = new MusicPlayer(Rich());
        var b = new MusicPlayer(Rich());
        a.Play();
        b.Play();
        await Assert.That(Render(a, 30_000).SequenceEqual(Render(b, 30_000))).IsTrue();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RenderingIsEquivalentAcrossArbitraryCallbackSizes(bool loop)
    {
        var whole = new MusicPlayer(Rich(0.5));
        whole.Play(loop);
        var expected = Render(whole, 40_000);

        var sliced = new MusicPlayer(Rich(0.5));
        sliced.Play(loop);
        var actual = new float[expected.Length];
        var sizes = new[] { 1, 7, 64, 128, 333, 1000, 4096 };
        for (int offset = 0, i = 0; offset < actual.Length; i++)
        {
            var frames = Math.Min(sizes[i % sizes.Length], (actual.Length - offset) / 2);
            sliced.Render(actual.AsSpan(offset, frames * 2));
            offset += frames * 2;
        }
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task LoopingStartsEveryNoteAgainAndCarriesTailsAcrossTheSeam()
    {
        var song = new Song(
            [new Instrument { Waveform = Waveform.Sine, Envelope = new Envelope(0.001, 0, 1, 0.3), Gain = 0.5 }],
            [new Note(0, 0.05, 69, 1, 0), new Note(0.45, 0.04, 81, 1, 0)],
            0.5);
        var player = new MusicPlayer(song);
        player.Play(loop: true);
        var first = Render(player, Rate / 2);
        var second = Render(player, Rate / 2);
        await Assert.That(player.State).IsEqualTo(MusicPlaybackState.Playing);
        // The second pass hears its first note again, over the first pass's last note still releasing.
        await Assert.That(Rms(second.AsSpan(0, 2000))).IsGreaterThan(Rms(first.AsSpan(0, 2000)));
        await Assert.That(player.Position.TotalSeconds).IsLessThan(0.01);
        // The seam itself is continuous.
        await Assert.That(MathF.Abs(second[0] - first[^2])).IsLessThan(0.1f);
    }

    [Test]
    public async Task PauseFreezesAndResumeContinuesTheExactSampleSequence()
    {
        var straight = new MusicPlayer(Rich());
        straight.Play();
        var expected = Render(straight, 20_000);

        var paused = new MusicPlayer(Rich());
        paused.Play();
        var head = Render(paused, 7_000);
        paused.Pause();
        var position = paused.Position;
        await Assert.That(Render(paused, 5_000).All(s => s == 0)).IsTrue();
        await Assert.That(paused.Position).IsEqualTo(position);
        paused.Resume();
        var tail = Render(paused, 13_000);
        await Assert.That(head.Concat(tail).SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task ASinglePlayStopsAtItsLengthAndClearsTheRest()
    {
        var player = new MusicPlayer(Simple(0.25));
        await Assert.That(player.LengthFrames).IsEqualTo(12_000L);
        await Assert.That(player.Length).IsEqualTo(TimeSpan.FromSeconds(0.25));
        player.Play(loop: false);
        var buffer = Enumerable.Repeat(1f, 20_000 * 2).ToArray();
        player.Render(buffer);
        await Assert.That(player.State).IsEqualTo(MusicPlaybackState.Stopped);
        await Assert.That(player.Position).IsEqualTo(player.Length);
        await Assert.That(buffer.AsSpan(12_000 * 2).ToArray().All(s => s == 0)).IsTrue();
        await Assert.That(Rms(buffer.AsSpan(0, 12_000 * 2))).IsGreaterThan(0.01);
        // Further renders stay silent until played again.
        await Assert.That(Render(player, 100).All(s => s == 0)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlayRestartsFromTheBeginning(bool pauseFirst)
    {
        var player = new MusicPlayer(Rich());
        player.Play();
        var first = Render(player, 10_000);
        Render(player, 3_333);
        if (pauseFirst) player.Pause();
        player.Play();
        await Assert.That(player.Position).IsEqualTo(TimeSpan.Zero);
        await Assert.That(Render(player, 10_000).SequenceEqual(first)).IsTrue();
    }

    [Test]
    public async Task StopSilencesAndRewinds()
    {
        var player = new MusicPlayer(Rich());
        player.Play();
        Render(player, 10_000);
        player.Stop();
        await Assert.That(player.State).IsEqualTo(MusicPlaybackState.Stopped);
        await Assert.That(player.Position).IsEqualTo(TimeSpan.Zero);
        await Assert.That(player.ActiveVoices).IsEqualTo(0);
    }

    [Test]
    public async Task VolumeScalesOutputAndRejectsInvalidValues()
    {
        var loud = new MusicPlayer(Simple());
        var quiet = new MusicPlayer(Simple()) { Volume = .25f };
        loud.Play();
        quiet.Play();
        var a = Render(loud, 4000);
        var b = Render(quiet, 4000);
        for (var i = 0; i < a.Length; i++)
            await Assert.That(MathF.Abs(a[i] * .25f - b[i])).IsLessThan(1e-6f);
        await Assert.That(() => quiet.Volume = 1.5f).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => quiet.Volume = float.NaN).Throws<ArgumentOutOfRangeException>();
        await Assert.That(quiet.Volume).IsEqualTo(.25f);
        await Assert.That(() => quiet.Render(new float[3])).Throws<ArgumentException>();
    }

    [Test]
    public async Task RenderDoesNotAllocateAfterWarmupIncludingLoopSeams()
    {
        var player = new MusicPlayer(Rich());
        var output = new float[2050];
        player.Play();
        for (var i = 0; i < 100; i++)
            player.Render(output); // > 2 s: every voice kind, delay, reverb and two seams
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 150; i++)
            player.Render(output);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0L);
    }

    [Test]
    public async Task VoicesRetireRatherThanPileUpLoopAfterLoop()
    {
        var player = new MusicPlayer(Rich(0.5));
        player.Play();
        Render(player, Rate * 2);
        var most = 0;
        for (var i = 0; i < 20; i++)
        {
            Render(player, Rate / 4);
            most = Math.Max(most, player.ActiveVoices);
        }
        // Each pass starts 20 notes; voices that piled up would add that many a pass.
        await Assert.That(most).IsLessThanOrEqualTo(40);
    }

    [Test]
    public async Task MoreNotesThanVoicesStealQuietlyWithoutFailing()
    {
        var notes = Enumerable.Range(0, 400).Select(i => new Note(i * 0.001, 1, 40 + i % 40, 0.5f, 0));
        var song = new Song([new Instrument { Waveform = Waveform.Sawtooth, Gain = 0.01, Envelope = Envelope.Held }], notes, 1);
        var player = new MusicPlayer(song);
        player.Play();
        var output = Render(player, Rate);
        await Assert.That(output.All(float.IsFinite)).IsTrue();
        await Assert.That(player.ActiveVoices).IsLessThanOrEqualTo(128);
    }

    [Test]
    public async Task AnArrangementFadesEachPartsVoicesAndKeepsThroughPlay()
    {
        var song = new Song(
            [
                new Instrument { Part = ScorePart.Bass, Waveform = Waveform.Sine, Envelope = Envelope.Held, Gain = 0.5 },
                new Instrument { Part = ScorePart.Melody, Waveform = Waveform.Square, Envelope = Envelope.Held, Gain = 0.5 },
            ],
            [new Note(0, 2, 45, 1, 0), new Note(0, 2, 72, 1, 1)],
            2);
        var whole = new MusicPlayer(song);
        whole.Play();
        var full = Render(whole, Rate / 2);

        var arranged = new MusicPlayer(song);
        float[] gains = [1, 1, 1, 1, 1, 1, 1, 0]; // melody out
        arranged.Arrange(gains, 0);
        arranged.Play();
        var bassOnly = Render(arranged, Rate / 2);
        await Assert.That(arranged.PartGain(ScorePart.Melody)).IsEqualTo(0f);
        await Assert.That(Rms(bassOnly)).IsLessThan(Rms(full)).And.IsGreaterThan(0.05);

        // A ramp gets half way in half its time, then all the way.
        arranged.Arrange([1, 1, 1, 1, 1, 1, 1, 1], 1);
        Render(arranged, Rate / 2);
        await Assert.That(arranged.PartGain(ScorePart.Melody)).IsGreaterThan(.45f).And.IsLessThan(.55f);
        Render(arranged, Rate / 2 + 128);
        await Assert.That(arranged.PartGain(ScorePart.Melody)).IsEqualTo(1f);

        await Assert.That(() => arranged.Arrange([1, 1], 0)).Throws<ArgumentException>();
        await Assert.That(() => arranged.Arrange([1, 1, 1, 1, 1, 1, 1, 2], 0)).Throws<ArgumentException>();
        await Assert.That(() => arranged.Arrange(gains, -1)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ASongOfNoNotesPlaysSilence()
    {
        var player = new MusicPlayer(new Song([], [], 0.5));
        player.Play();
        await Assert.That(Render(player, Rate).All(s => s == 0)).IsTrue();
    }

    [Test]
    public async Task SongsRefuseWhatTheyCannotPlay()
    {
        Instrument plain = new();
        await Assert.That(() => new Song([plain], [new Note(1, 0.1, 60, 1, 0)], 1)).Throws<ArgumentException>(); // starts at the end
        await Assert.That(() => new Song([plain], [new Note(0, 0.1, 60, 1, 1)], 1)).Throws<ArgumentException>(); // no instrument 1
        await Assert.That(() => new Song([plain], [new Note(0, 0.1, 60, 2, 0)], 1)).Throws<ArgumentException>(); // velocity
        await Assert.That(() => new Song([plain with { Sends = new Sends(0) { Reverb = 0.5 } }], [], 1)).Throws<ArgumentException>(); // no bus to send to
        await Assert.That(() => new Song([plain with { Unison = new Unison(0, 0, 0) }], [], 1)).Throws<ArgumentException>();
        // A modulation has one source, and a cutoff modulation names a filter the instrument has.
        await Assert.That(() => new Song([plain with { Modulations = [new Modulation(ModulationTarget.Pitch, 1)] }], [], 1)).Throws<ArgumentException>();
        await Assert.That(() => new Song([plain with { Modulations = [Modulation.ByLfo(ModulationTarget.Cutoff, 1, 1)] }], [], 1)).Throws<ArgumentException>();
        await Assert.That(() => new Song([plain with { Modulations = [new Modulation(ModulationTarget.Pitch, 1) { Lfo = new Lfo(1), Envelope = Envelope.Held }] }], [], 1)).Throws<ArgumentException>();
        await Assert.That(() => new Song([plain with { Envelope = new Envelope(-1, 0, 1, 0) }], [], 1)).Throws<ArgumentException>();
        await Assert.That(() => new Song([plain], [], 0)).Throws<ArgumentException>();
        await Assert.That(() => new Song([plain], [], 1, [new EffectBus { DelayFeedback = 1 }])).Throws<ArgumentException>();
        await Assert.That(() => new MusicPlayer(new Song([], [], 1e-6))).Throws<ArgumentException>();
    }

    [Test]
    public async Task NotesAreKeptInStartOrder()
    {
        var song = new Song([new Instrument()], [new Note(0.5, 0, 60, 1, 0), new Note(0.1, 0, 61, 1, 0), new Note(0.5, 0, 62, 1, 0)], 1);
        await Assert.That(song.Notes.Select(n => n.Pitch).SequenceEqual([61.0, 60.0, 62.0])).IsTrue();
    }
}
