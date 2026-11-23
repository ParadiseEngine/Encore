using System.Numerics;
using System.Text.Json;
using Encore.Audio;

namespace Encore.Tests;

/// <summary>The engine and mixer over a small sound table written for the test: what reaches the speakers.</summary>
public sealed class MixerTests
{
    private const int Rate = AudioMixer.SampleRate;
    private const ulong Listener = 1, Emitter = 2;

    [Test]
    public async Task APositionedSoundPansToTheListenersSideAndFadesWithDistance()
    {
        using var mix = new Mix();
        var right = mix.Level(Vector3.UnitX * 3, out var rightLeft);
        var left = mix.Level(-Vector3.UnitX * 3, out var leftLeft);
        var middle = mix.Level(Vector3.Zero, out var middleLeft);
        var far = mix.Level(Vector3.UnitX * 14, out _);
        var gone = mix.Level(Vector3.UnitX * 25, out var goneLeft);
        // Right of the listener is louder on the right; left of it, on the left; at it, even.
        await Assert.That(right).IsGreaterThan(rightLeft * 2);
        await Assert.That(leftLeft).IsGreaterThan(left * 2);
        await Assert.That(MathF.Abs(middle - middleLeft)).IsLessThan(middle * .01f);
        // Farther than its minimum distance it is quieter, and past its maximum it is silent.
        await Assert.That(far).IsLessThan(right * .5f);
        await Assert.That(gone + goneLeft).IsEqualTo(0f);
    }

    [Test]
    public async Task ALoopPlaysOncePerEmitterAndStopsWithAFadeOnItsStopEvent()
    {
        using var mix = new Mix();
        mix.Engine.PostEvent("Play_Drone", Emitter);
        mix.Engine.PostEvent("Play_Drone", Emitter);
        mix.Commit();
        mix.Render(.5f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
        var playing = mix.Rms(.2f);
        mix.Engine.PostEvent(AudioEvents.Stop("Play_Drone"), Emitter);
        mix.Commit();
        var fading = mix.Rms(.1f);
        mix.Render(.5f);
        await Assert.That(playing).IsGreaterThan(.05f);
        await Assert.That(fading).IsGreaterThan(0f);
        await Assert.That(fading).IsLessThan(playing);
        await Assert.That(mix.Rms(.1f)).IsEqualTo(0f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(0);
        // Stopped, it may start again.
        mix.Engine.PostEvent("Play_Drone", Emitter);
        mix.Commit();
        mix.Render(.3f);
        await Assert.That(mix.Rms(.1f)).IsGreaterThan(.05f);
    }

    [Test]
    public async Task UnregisteringAnEmitterFadesItsLoopsButLetsItsOneShotsPlayOutWhereItStood()
    {
        using var mix = new Mix();
        mix.Engine.PostEvent("Play_Hum", Emitter);
        mix.Commit();
        mix.Render(.3f);
        mix.Engine.PostEvent("Play_Tone", Emitter);
        mix.Engine.UnregisterObject(Emitter);
        mix.Commit();
        mix.Render(.4f);
        // The hum has faded out; the tone (0.5 s) still sounds, on the right where the emitter stood.
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
        var right = mix.Rms(.05f, channel: 1);
        await Assert.That(right).IsGreaterThan(.05f);
        mix.Render(.2f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(0);
        await Assert.That(() => mix.Engine.PostEvent("Play_Tone", Emitter)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PausingALoopHoldsItsPlaceAndResumingContinuesIt()
    {
        using var mix = new Mix();
        mix.Engine.PostEvent("Play_Hum", Emitter);
        mix.Commit();
        mix.Render(.3f);
        mix.Engine.PostEvent(AudioEvents.Pause("Play_Hum"), Emitter);
        mix.Commit();
        mix.Render(.2f);
        await Assert.That(mix.Rms(.5f)).IsEqualTo(0f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
        mix.Engine.PostEvent(AudioEvents.Resume("Play_Hum"), Emitter);
        mix.Commit();
        mix.Render(.2f);
        await Assert.That(mix.Rms(.1f)).IsGreaterThan(.05f);
    }

    [Test]
    public async Task APausedLoopThatIsStoppedOrLosesItsEmitterFreesItsVoice()
    {
        using var mix = new Mix();
        mix.Engine.PostEvent("Play_Drone", Emitter);
        mix.Commit();
        mix.Render(.3f);
        mix.Engine.PostEvent(AudioEvents.Pause("Play_Drone"), Emitter);
        mix.Commit();
        mix.Render(.2f);
        mix.Engine.PostEvent(AudioEvents.Stop("Play_Drone"), Emitter);
        mix.Commit();
        mix.Render(.1f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(0);
        // As when a level's emitters go on travel while the cabinet holds the record paused.
        mix.Engine.PostEvent("Play_Drone", Emitter);
        mix.Commit();
        mix.Render(.3f);
        mix.Engine.PostEvent(AudioEvents.Pause("Play_Drone"), Emitter);
        mix.Commit();
        mix.Render(.2f);
        mix.Engine.UnregisterObject(Emitter);
        mix.Commit();
        mix.Render(.1f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(0);
    }

    [Test]
    public async Task AControlEventPlaysItsOwnSoundsAfterActingOnThePlayEventsLoops()
    {
        using var mix = new Mix();
        mix.Engine.PostEvent("Play_Hum", Emitter);
        mix.Commit();
        mix.Render(.3f);
        mix.Engine.PostEvent("Stop_Hum", Emitter);
        mix.Commit();
        mix.Render(.5f);
        // The hum has faded and its stop sound (0.8 s) is still playing.
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
        await Assert.That(mix.Rms(.1f)).IsGreaterThan(.05f);
    }

    [Test]
    public async Task OneShotsWithinTheMinimumIntervalAreDroppedAndTheVoiceCapStealsTheOldest()
    {
        using var mix = new Mix();
        for (var i = 0; i < 5; i++) mix.Engine.PostEvent("Play_Tone", Emitter);
        mix.Commit();
        mix.Render(.01f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
        for (var i = 0; i < 4; i++)
        {
            mix.Engine.PostEvent("Play_Tone", Emitter);
            mix.Commit();
            mix.Render(.06f);
        }
        // Two at most (the table's maxVoices): the oldest fade out as new ones start.
        await Assert.That(mix.Mixer.ActiveVoices).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task APoolStartsOneOfItsSoundsPerIntervalAndKeepsTheNearestWhenItsVoicesAreFull()
    {
        using var mix = new Mix();
        // Two steps in one frame: the pool's interval lets one through.
        mix.Engine.PostEventAt("Play_Step", Vector3.UnitX * 12);
        mix.Engine.PostEventAt("Play_Step", Vector3.UnitX * 13);
        mix.Commit();
        mix.Render(.06f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
        mix.Engine.PostEventAt("Play_Step", Vector3.UnitX * 14);
        mix.Commit();
        var two = mix.Rms(.06f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(2);
        // Both voices taken, a farther step is dropped and the pool sounds as it did.
        mix.Engine.PostEventAt("Play_Step", Vector3.UnitX * 16);
        mix.Commit();
        await Assert.That(mix.Rms(.06f)).IsEqualTo(two).Within(two * .05f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(2);
        // A nearer one takes the quietest's place.
        mix.Engine.PostEventAt("Play_Step", Vector3.UnitX);
        mix.Commit();
        await Assert.That(mix.Rms(.06f)).IsGreaterThan(two * 2);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(2);
    }

    [Test]
    public async Task PastTheVoiceCapANewSoundFadesTheOldestOneShotOutRatherThanCuttingIt()
    {
        using var mix = new Mix(voices: 1);
        mix.Engine.PostEvent("Play_Blast", Emitter);
        mix.Commit();
        // Ending near the blast's trough, where cutting it would jump by most of its level.
        mix.Render(.0977f);
        mix.Engine.PostEvent("Play_Tone", Emitter);
        mix.Commit();
        mix.Render(.05f);
        // No sample moves further than the two sines themselves can (about 0.04 a sample here).
        await Assert.That(mix.LargestJump()).IsLessThan(.1f);
        await Assert.That(mix.Mixer.ActiveVoices).IsEqualTo(1);
    }

    [Test]
    public async Task TheLimiterKeepsAPileOfLoudSoundsUnderTheCeiling()
    {
        using var mix = new Mix(voices: 128);
        for (var i = 0; i < 100; i++)
        {
            mix.Engine.PostEventAt("Play_Blast", Vector3.Zero);
            mix.Commit();
            mix.Render(1f / 60);
        }
        await Assert.That(mix.Mixer.Peak).IsLessThanOrEqualTo(.8915f);
        await Assert.That(mix.Mixer.Peak).IsGreaterThan(.5f);
    }

    [Test]
    public async Task TheAmbienceBedPlaysUnpositionedOnTheListenerAndFadesWhenItIsUnregistered()
    {
        using var mix = new Mix();
        mix.Engine.PostEvent("Play_Bed", Listener);
        mix.Commit();
        mix.Render(2.5f);
        var left = mix.Rms(.2f, channel: 0);
        var right = mix.Rms(.2f, channel: 1);
        await Assert.That(left).IsGreaterThan(.05f);
        await Assert.That(MathF.Abs(left - right)).IsLessThan(left * .05f);
        mix.Engine.UnregisterObject(Listener);
        mix.Commit();
        mix.Render(2.5f);
        await Assert.That(mix.Rms(.2f)).IsEqualTo(0f);
    }

    [Test]
    public async Task UnknownEventsLoopsAtAPointAndUnpositionedEmittersAreRefused()
    {
        using var mix = new Mix();
        await Assert.That(() => mix.Engine.PostEvent("Play_Nothing", Emitter)).Throws<KeyNotFoundException>();
        await Assert.That(() => mix.Engine.PostEvent("Stop_Nothing", Emitter)).Throws<KeyNotFoundException>();
        await Assert.That(() => mix.Engine.PostEventAt("Play_Hum", Vector3.Zero)).Throws<InvalidOperationException>();
        mix.Engine.RegisterObject(9);
        await Assert.That(() => mix.Engine.PostEvent("Play_Tone", 9)).Throws<InvalidOperationException>();
        await Assert.That(() => mix.Engine.RegisterObject(9)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TheMusicPlaysTheRequestedScoreAndFadesOutWhenNoneIsWanted()
    {
        using var mix = new Mix();
        mix.Engine.PlayMusic("themes/funk-opus55-b/75-afro-funk-market-loop", fighting: false);
        mix.Commit();
        mix.Render(3f);
        await Assert.That(mix.Rms(1f)).IsGreaterThan(.01f);
        // Asking again for the score playing does not start it over: it keeps sounding without a fade.
        mix.Engine.PlayMusic("themes/funk-opus55-b/75-afro-funk-market-loop", fighting: false);
        mix.Commit();
        await Assert.That(mix.Rms(.5f)).IsGreaterThan(.01f);
        mix.Engine.PlayMusic(null, fighting: false);
        mix.Commit();
        mix.Render(3f);
        await Assert.That(mix.Rms(.5f)).IsEqualTo(0f);
    }

    [Test]
    public async Task AskingForWhatTheMixerHasBeforeTheNextScoreLoadsDropsThatLoad()
    {
        const string Market = "themes/funk-opus55-b/75-afro-funk-market-loop", Garage = "themes/disco-opus55-b/65-garage-disco-dub-break";
        using var mix = new Mix(loadInline: false);
        mix.Engine.PlayMusic(Market, fighting: false);
        await mix.Loaded();
        mix.Render(3f);
        // Back to the score playing while another loads: it goes on, and nothing loads to replace it.
        mix.Engine.PlayMusic(Garage, fighting: false);
        mix.Engine.PlayMusic(Market, fighting: false);
        await Assert.That(mix.Engine.LoadingMusic).IsFalse();
        // Back to silence while a score loads: the one fading out keeps fading rather than being cut.
        mix.Engine.PlayMusic(null, fighting: false);
        mix.Commit();
        mix.Render(.2f);
        mix.Engine.PlayMusic(Garage, fighting: false);
        mix.Engine.PlayMusic(null, fighting: false);
        mix.Commit();
        await Assert.That(mix.Engine.LoadingMusic).IsFalse();
        await Assert.That(mix.Rms(.2f)).IsGreaterThan(.01f);
    }

    [Test]
    public async Task TheTableRefusesAPositionedStereoSoundAMasterNotAt48KilohertzALoopInAPoolAndABattleArrangementOfNoPart()
    {
        await Assert.That(() => Mix.Bank(stereoPositioned: true)).Throws<InvalidDataException>();
        await Assert.That(() => Mix.Bank(rate: 44_100)).Throws<InvalidDataException>();
        await Assert.That(() => Mix.Bank(pooledLoop: true)).Throws<InvalidDataException>();
        await Assert.That(() => Mix.Bank(battleParts: ["kick", "drums"])).Throws<InvalidDataException>();
        await Assert.That(() => Mix.Bank(battleParts: ["bass", "bass"])).Throws<InvalidDataException>();
    }

    [Test]
    public async Task AFightLeavesOnlyTheTablesPartsQuieterAndTheWholeScoreComesBackAfter()
    {
        // The table's battleMusic (design.md 戰鬥只留下節奏組): as a fight starts the parts it does not name fade out over its leave
        // time and its parts settle at its volume; once the fight is over every part comes back over its return time.
        const string Fusion = "themes/funk-opus55-c/81-fusion-crosswind-modal", Market = "themes/funk-opus55-b/75-afro-funk-market-loop";
        // (Whole and Fight: the arrangements below, at the table's −6 dB.)
        using var mix = new Mix(battle: true);
        mix.Engine.PlayMusic(Fusion, fighting: false);
        mix.Commit();
        mix.Render(3f);
        var whole = mix.Rms(4f);
        await Assert.That(Arrangement(mix)).IsEqualTo(Whole);

        mix.Engine.PlayMusic(Fusion, fighting: true);
        mix.Commit();
        mix.Render(Mix.LeaveSeconds / 2);
        await Assert.That(mix.Mixer.PartGain(ScorePart.Melody)).IsGreaterThan(.3f).And.IsLessThan(.7f);
        mix.Render(Mix.LeaveSeconds);
        await Assert.That(Arrangement(mix)).IsEqualTo(Fight);
        // Its rhythm parts still sound, quieter than the whole score.
        await Assert.That(mix.Rms(4f)).IsGreaterThan(.005f).And.IsLessThan(whole);

        // A score that comes in mid-fight starts arranged for it.
        mix.Engine.PlayMusic(Market, fighting: true);
        mix.Commit();
        mix.Render(.01f);
        await Assert.That(Arrangement(mix)).IsEqualTo(Fight);

        mix.Engine.PlayMusic(Market, fighting: false);
        mix.Commit();
        mix.Render(Mix.ReturnSeconds / 2);
        await Assert.That(mix.Mixer.PartGain(ScorePart.Keys)).IsGreaterThan(.3f).And.IsLessThan(.7f);
        mix.Render(Mix.ReturnSeconds);
        await Assert.That(Arrangement(mix)).IsEqualTo(Whole);

        // A table without battleMusic leaves every score whole through a fight.
        using var plain = new Mix();
        plain.Engine.PlayMusic(Fusion, fighting: true);
        plain.Commit();
        plain.Render(2f);
        await Assert.That(Arrangement(plain)).IsEqualTo(Whole);
    }

    [Test]
    public async Task AScoreGivingWayLeavesAsTheFightArrangedItAndTheNextComesInWhole()
    {
        // Only the score that stays comes back whole after a fight: one a knock-down silences or a clear replaces fades
        // out as the fight arranged it, never bringing its other parts back on the way out. (Each fight first plays 3 s,
        // past the 2.5 s fade-in, so the score fading out is heard to the end of its 2.5 s fade.)
        const string Fusion = "themes/funk-opus55-c/81-fusion-crosswind-modal", Market = "themes/funk-opus55-b/75-afro-funk-market-loop";
        using var down = new Mix(battle: true);
        down.Engine.PlayMusic(Fusion, fighting: true);
        down.Commit();
        down.Render(3f);
        down.Engine.PlayMusic(null, fighting: false);
        down.Commit();
        for (var i = 0; i < 5; i++)
        {
            down.Render(.4f);
            await Assert.That(Arrangement(down, leaving: true)).IsEqualTo(Fight);
        }

        using var cleared = new Mix(battle: true);
        cleared.Engine.PlayMusic(Fusion, fighting: true);
        cleared.Commit();
        cleared.Render(3f);
        cleared.Engine.PlayMusic(Market, fighting: false);
        cleared.Commit();
        cleared.Render(Mix.ReturnSeconds + .2f);
        await Assert.That(Arrangement(cleared, leaving: true)).IsEqualTo(Fight);
        await Assert.That(Arrangement(cleared)).IsEqualTo(Whole);
    }

    [Test]
    public async Task AFightsScoreKeepsItsArrangementUntilTheNextLoadsAndComesBackWholeIfAskedBackFirst()
    {
        const string Fusion = "themes/funk-opus55-c/81-fusion-crosswind-modal", Market = "themes/funk-opus55-b/75-afro-funk-market-loop";
        var market = new TaskCompletionSource<MusicPlayer>();
        Task<MusicPlayer> Scores(string score) => score == Market ? market.Task : Task.FromResult(new MusicPlayer(ScoreFile(score)));
        using var mix = new Mix(battle: true, scores: Scores);
        mix.Engine.PlayMusic(Fusion, fighting: true);
        mix.Commit();
        mix.Render(3f);
        // Cleared into a score still loading: the fight's score plays on as the fight arranged it, longer than a return takes.
        mix.Engine.PlayMusic(Market, fighting: false);
        mix.Commit();
        mix.Render(Mix.ReturnSeconds + .2f);
        await Assert.That(Arrangement(mix)).IsEqualTo(Fight);
        market.SetResult(new MusicPlayer(ScoreFile(Market)));
        mix.Commit();
        mix.Render(.2f);
        await Assert.That(Arrangement(mix, leaving: true)).IsEqualTo(Fight);
        await Assert.That(Arrangement(mix)).IsEqualTo(Whole);

        // Asked back to the score still playing before the next one loads: it stays, so it comes back whole.
        var never = new TaskCompletionSource<MusicPlayer>();
        using var back = new Mix(battle: true, scores: score => score == Market ? never.Task : Task.FromResult(new MusicPlayer(ScoreFile(score))));
        back.Engine.PlayMusic(Fusion, fighting: true);
        back.Commit();
        back.Render(3f);
        back.Engine.PlayMusic(Market, fighting: false);
        back.Commit();
        back.Render(.2f);
        back.Engine.PlayMusic(Fusion, fighting: false);
        back.Commit();
        back.Render(Mix.ReturnSeconds + .2f);
        await Assert.That(Arrangement(back)).IsEqualTo(Whole);
    }

    private const string Whole = "kick 1 snare 1 hats 1 bass 1 keys 1 pad 1 melody 1", Fight = "kick 0.5 snare 0.5 hats 0 bass 0.5 keys 0 pad 0 melody 0";

    private static string ScoreFile(string score) => MixerEngine.ScoreFile(TestScenes.RepositoryFile("assets/audio/scores"), score);

    /// <summary>Each part's gain, to one decimal (−6 dB is 0.5), in the score playing or the one fading out under it.</summary>
    private static string Arrangement(Mix mix, bool leaving = false) => string.Join(" ", Enumerable.Range((int)ScorePart.Kick, 7).Select(p =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{((ScorePart)p).ToString().ToLowerInvariant()} {MathF.Round(leaving ? mix.Mixer.LeavingPartGain((ScorePart)p) : mix.Mixer.PartGain((ScorePart)p), 1)}")));

    [Test]
    public async Task ARecordingReadsBackAsTheSamplesWritten()
    {
        var path = Path.Combine(Path.GetTempPath(), $"encore-wav-{Guid.NewGuid():N}.wav");
        try
        {
            float[] written = [0, .5f, -.5f, 1, -1, .25f];
            using (var file = File.Create(path))
            using (var writer = new WavWriter(file, 2))
                writer.Append(written);
            var clip = WavFile.Read(path);
            await Assert.That(clip.Channels).IsEqualTo(2);
            await Assert.That(clip.Frames).IsEqualTo(3);
            for (var i = 0; i < written.Length; i++)
                await Assert.That(MathF.Abs(clip.Samples[i] / 32767f - written[i])).IsLessThan(1e-4f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A table of seven sounds in a temporary folder: a positioned 0.5 s tone (two voices at most, 50 ms
    /// apart), the same tone as a step in a pool of two voices 50 ms apart, a positioned hum loop with a
    /// stop sound of its own, a drone loop without one, a loud blast and a stereo ambience bed; an engine
    /// and a mixer over it, the listener at the origin facing −Z (its right is +X) and an emitter 3 m to its right.
    /// </summary>
    private sealed class Mix : IDisposable
    {
        private readonly string _folder;
        private float[] _last = [], _before = [];

        /// <param name="loadInline">False loads scores off the test's thread, as the game does.</param>
        /// <param name="battle">Whether the table arranges the music for a fight (kick, snare and bass at <see cref="BattleVolumeDb"/>).</param>
        /// <param name="scores">Loads a score by its name; null loads it from the repository's scores (as <paramref name="loadInline"/> says).</param>
        public Mix(int voices = 64, bool loadInline = true, bool battle = false, Func<string, Task<MusicPlayer>>? scores = null)
        {
            (_folder, var bank) = Bank(battleParts: battle ? ["kick", "snare", "bass"] : null);
            Mixer = new AudioMixer(bank, voices);
            Engine = new MixerEngine(bank, Mixer, scores ?? MixerEngine.ScoresIn(TestScenes.RepositoryFile("assets/audio/scores"), loadInline));
            Engine.RegisterObject(Listener);
            Engine.SetListener(Listener);
            Engine.SetPosition(Listener, AudioPose.LookingAt(new Vector3(0, 0, 0), -Vector3.UnitZ));
            Engine.RegisterObject(Emitter);
            Engine.SetPosition(Emitter, new AudioPose(Vector3.UnitX * 3, Vector3.UnitZ, Vector3.UnitY));
            Commit();
        }

        public AudioMixer Mixer { get; }
        public MixerEngine Engine { get; }

        public void Commit() => Engine.Commit();

        /// <summary>Commits until the score asked for has loaded and been handed to the mixer.</summary>
        public async Task Loaded()
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            for (Commit(); Engine.LoadingMusic; Commit())
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The score did not load.");
                await Task.Delay(10);
            }
        }

        /// <summary>Renders <paramref name="seconds"/> and keeps the last block.</summary>
        public void Render(float seconds)
        {
            _before = _last;
            _last = new float[(int)(seconds * Rate) * AudioMixer.Channels];
            Mixer.Render(_last);
        }

        /// <summary>The largest jump between a channel's neighbouring samples in the last block, its first frame measured from the block before.</summary>
        public float LargestJump()
        {
            var largest = 0f;
            for (var i = 0; i < _last.Length; i++)
            {
                var previous = i >= AudioMixer.Channels ? _last[i - AudioMixer.Channels]
                    : _before.Length > 0 ? _before[_before.Length - AudioMixer.Channels + i] : 0;
                largest = MathF.Max(largest, MathF.Abs(_last[i] - previous));
            }
            return largest;
        }

        /// <summary>Renders <paramref name="seconds"/> and returns their RMS, both channels or one.</summary>
        public float Rms(float seconds, int channel = -1)
        {
            Render(seconds);
            double sum = 0;
            var count = 0;
            for (var i = 0; i < _last.Length; i++)
            {
                if (channel >= 0 && i % 2 != channel) continue;
                sum += _last[i] * _last[i];
                count++;
            }
            return (float)Math.Sqrt(sum / count);
        }

        /// <summary>The tone's right-channel RMS from a fresh emitter at <paramref name="position"/>, and its left's.</summary>
        public float Level(Vector3 position, out float left)
        {
            const ulong id = 50;
            Engine.RegisterObject(id);
            Engine.SetPosition(id, new AudioPose(position, Vector3.UnitZ, Vector3.UnitY));
            Engine.PostEvent("Play_Tone", id);
            Engine.UnregisterObject(id);
            Commit();
            left = Rms(.2f, channel: 0);
            var samples = _last;
            double right = 0;
            for (var i = 1; i < samples.Length; i += 2) right += samples[i] * samples[i];
            Render(.4f);
            return (float)Math.Sqrt(right / (samples.Length / 2));
        }

        /// <summary>The table's fight arrangement, when it has one: its volume and its leave and return times.</summary>
        public const float BattleVolumeDb = -6, LeaveSeconds = .5f, ReturnSeconds = 1f;

        /// <summary>Writes the table to a temporary folder and loads it; the arguments break it for the refusal test, or give it a fight's arrangement of <paramref name="battleParts"/>.</summary>
        public static (string Folder, SoundBank Bank) Bank(bool stereoPositioned = false, int rate = Rate, bool pooledLoop = false, string[]? battleParts = null)
        {
            var folder = Path.Combine(Path.GetTempPath(), $"encore-sfx-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            Write(folder, "tone.wav", Sine(.5f, 440, .5f), 1, rate);
            Write(folder, "hum.wav", Sine(1, 220, .5f), 1, rate);
            Write(folder, "click.wav", Sine(.8f, 880, .5f), 1, rate);
            Write(folder, "drone.wav", Sine(1, 165, .5f), 1, rate);
            Write(folder, "blast.wav", Sine(.5f, 110, 1), 1, rate);
            Write(folder, "bed.wav", Sine(2, 330, .4f), 2, rate);
            object Sound(string id, string @event, string kind, string bus, bool positioned, string file, int maxVoices = 4, float interval = 0, string pool = "") => new
            {
                id, @event, kind, bus, positioned, volumeDb = 0.0, pitchCents = 0, maxVoices, minIntervalSeconds = interval,
                minDistance = 2.0, maxDistance = 20.0, pool, files = new[] { file },
            };
            var table = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1, ["kind"] = "encore.sound-table", ["sampleRate"] = Rate, ["generator"] = "MixerTests",
                ["pools"] = new[] { new { id = "steps", maxVoices = 2, minIntervalSeconds = .05 } },
                ["sounds"] = new[]
                {
                    Sound("tone", "Play_Tone", "one-shot", "effects", true, "tone.wav", maxVoices: 2, interval: .05f),
                    Sound("step", "Play_Step", "one-shot", "effects", true, "tone.wav", pool: "steps"),
                    Sound("hum", "Play_Hum", "loop", "effects", true, "hum.wav", pool: pooledLoop ? "steps" : ""),
                    Sound("hum-stop", "Stop_Hum", "one-shot", "effects", true, "click.wav"),
                    Sound("drone", "Play_Drone", "loop", "effects", true, "drone.wav"),
                    Sound("blast", "Play_Blast", "one-shot", "effects", true, "blast.wav", maxVoices: 128),
                    Sound("bed", "Play_Bed", "loop", "ambience", stereoPositioned, "bed.wav"),
                },
            };
            if (battleParts is not null)
                table["battleMusic"] = new { parts = battleParts, volumeDb = BattleVolumeDb, leaveSeconds = LeaveSeconds, returnSeconds = ReturnSeconds };
            File.WriteAllText(Path.Combine(folder, SoundBank.TableFile), JsonSerializer.Serialize(table));
            try
            {
                return (folder, SoundBank.Load(folder));
            }
            catch
            {
                Directory.Delete(folder, true);
                throw;
            }
        }

        private static float[] Sine(float seconds, float hertz, float amplitude) =>
            [.. Enumerable.Range(0, (int)(seconds * Rate)).Select(i => amplitude * MathF.Sin(2 * MathF.PI * hertz * i / Rate))];

        private static void Write(string folder, string name, float[] mono, int channels, int rate)
        {
            using var file = File.Create(Path.Combine(folder, name));
            using (var writer = new WavWriter(file, channels))
                writer.Append(channels == 1 ? mono : [.. mono.SelectMany(s => new[] { s, s })]);
            if (rate == Rate) return;
            // Rewrite the header's rate for the refusal test.
            file.Position = 24;
            file.Write(BitConverter.GetBytes(rate));
        }

        public void Dispose()
        {
            Engine.Dispose();
            Directory.Delete(_folder, true);
        }
    }
}
