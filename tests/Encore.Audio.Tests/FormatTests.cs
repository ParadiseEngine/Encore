using System.Text;
using Encore.Audio;
using Encore.Audio.Music;
using Encore.Audio.Music.Formats;

namespace Encore.Tests;

/// <summary>The song formats, this engine's JSON and Standard MIDI Files, from in-memory documents and the bundled assets.</summary>
public sealed class FormatTests
{
    private static Song Read(string text) => MusicFormats.Read(Encoding.UTF8.GetBytes(text));

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    private const string MinimalSong = """
        {
          "format": "encore.song", "version": 1, "bpm": 120, "length": 4,
          "instruments": [ { "name": "lead", "part": "melody", "wave": "pulse", "pulseWidth": 0.3,
                             "envelope": { "attack": 0.01 },
                             "filters": [ { "mode": "lowpass", "cutoff": 2000 } ],
                             "modulations": [ { "target": "cutoff", "filter": 0, "amount": 1.5, "envelope": { "decay": 0.2, "sustain": 0 } },
                                              { "target": "pitch", "amount": 0.2, "lfo": { "rate": 5, "shape": "triangle" } } ],
                             "sends": { "bus": 0, "delay": 0.25 } } ],
          "effects": [ { "delayBeats": 0.75 } ],
          "notes": [ [1, 0.5, 0, 64, 0.5], [0, 1, 0] ]
        }
        """;

    [Test]
    public async Task TheSongFormatReadsBeatsAtItsTempo()
    {
        var song = Read(MinimalSong);
        await Assert.That(song.Length).IsEqualTo(2.0);
        await Assert.That(song.Notes.Count).IsEqualTo(2);
        await Assert.That(song.Notes[0]).IsEqualTo(new Note(0, 0.5, null, 1, 0));
        await Assert.That(song.Notes[1]).IsEqualTo(new Note(0.5, 0.25, 64, 0.5f, 0));
        var lead = song.Instruments[0];
        await Assert.That(lead.Part).IsEqualTo(ScorePart.Melody);
        await Assert.That(lead.Waveform).IsEqualTo(Waveform.Pulse);
        await Assert.That(lead.PulseWidth).IsEqualTo(0.3);
        // An envelope's missing fields keep the default envelope's.
        await Assert.That(lead.Envelope).IsEqualTo(Envelope.Held with { Attack = 0.01 });
        var cutoff = lead.Modulations[0];
        await Assert.That(cutoff.Target).IsEqualTo(ModulationTarget.Cutoff);
        await Assert.That(cutoff.Amount).IsEqualTo(1.5);
        await Assert.That(cutoff.Envelope).IsEqualTo(new Envelope(Envelope.Held.Attack, 0.2, 0, Envelope.Held.Release));
        var vibrato = lead.Modulations[1];
        await Assert.That(vibrato.Lfo).IsEqualTo(new Lfo(5) { Shape = LfoShape.Triangle });
        // Effect times in beats become seconds at the tempo.
        await Assert.That(song.Effects[0].DelaySeconds).IsEqualTo(0.375);
        await Assert.That(lead.Sends).IsEqualTo(new Sends(0) { Delay = 0.25 });
    }

    [Test]
    public async Task WritingASongAndReadingItBackLosesNothing()
    {
        var songs = new[] { MusicPlayerTests.Rich(), Read(MinimalSong), MusicFormats.Load(BundledScore("themes/funk-opus55-b/75-afro-funk-market-loop.song.json")), MusicFormats.Load(BundledScore("examples/first-light.song.json")) };
        foreach (var song in songs)
        {
            var written = SongJsonFormat.Write(song);
            var again = Read(written);
            await Assert.That(SongJsonFormat.Write(again)).IsEqualTo(written);
            await Assert.That(again.Notes.SequenceEqual(song.Notes)).IsTrue();
            await Assert.That(again.Length).IsEqualTo(song.Length);
            await Assert.That(again.Seed).IsEqualTo(song.Seed);
            // The same song plays the same samples.
            var a = new MusicPlayer(song);
            var b = new MusicPlayer(again);
            a.Play();
            b.Play();
            var left = new float[9600];
            var right = new float[9600];
            a.Render(left);
            b.Render(right);
            await Assert.That(left.SequenceEqual(right)).IsTrue();
        }
    }

    [Test]
    [Arguments("""{"format":"encore.song","version":2,"bpm":120,"length":4,"instruments":[],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[],"notes":[],"tempo":3}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"wave":"saw"}],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"gian":1}],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{}],"notes":[[0,1,1]]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{}],"notes":[[4,1,0]]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"reverb":0.2}],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"effects":[{"delayBeats":1,"delaySeconds":1}],"instruments":[],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"bpm":100,"length":4,"instruments":[],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[],"notes":[] """)]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"modulations":[{"target":"pitch","amount":1}]}],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"modulations":[{"target":"volume","amount":1,"lfo":{"rate":1}}]}],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"modulations":[{"target":"cutoff","amount":1,"lfo":{"rate":1}}]}],"notes":[]}""")]
    [Arguments("""{"format":"encore.song","version":1,"bpm":120,"length":4,"instruments":[{"unison":{"detune":1}}],"notes":[]}""")]
    public async Task TheSongFormatRefusesMistakes(string json)
    {
        await Assert.That(() => Read(json)).Throws<InvalidDataException>();
    }

    /// <summary>A Standard MIDI File of the given tracks (each its events after MTrk's length).</summary>
    internal static byte[] Midi(int format, int division, params byte[][] tracks)
    {
        var bytes = new List<byte>();
        bytes.AddRange("MThd"u8.ToArray());
        bytes.AddRange([0, 0, 0, 6, 0, (byte)format, 0, (byte)tracks.Length, (byte)(division >> 8), (byte)division]);
        foreach (var track in tracks)
        {
            bytes.AddRange("MTrk"u8.ToArray());
            bytes.AddRange([(byte)(track.Length >> 24), (byte)(track.Length >> 16), (byte)(track.Length >> 8), (byte)track.Length]);
            bytes.AddRange(track);
        }
        return [.. bytes];
    }

    // 480 ticks a beat. The tempo track: 120 bpm, then 60 bpm from tick 960 (1 s), ending at tick 1440 (2 s).
    private static readonly byte[] TempoTrack = [0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, 0x87, 0x40, 0xFF, 0x51, 0x03, 0x0F, 0x42, 0x40, 0x83, 0x60, 0xFF, 0x2F, 0x00];

    // Program 33 (a bass) on channel 1: C4 from 0 to 0.5 s (ended by a running-status note on at velocity 0), then
    // D4 at velocity 127 from 0.5 s to tick 1440 (2 s, across the tempo change).
    private static readonly byte[] BassTrack = [0x00, 0xC0, 0x21, 0x00, 0x90, 0x3C, 0x64, 0x83, 0x60, 0x3C, 0x00, 0x00, 0x3E, 0x7F, 0x87, 0x40, 0x80, 0x3E, 0x40, 0x00, 0xFF, 0x2F, 0x00];

    // Drums on channel 10: a kick from 0 to 0.25 s, a closed hat from 0.25 to 0.5 s. Then a piano on channel 2 with
    // the sustain pedal down: E4 from 0.5 s, let go at 0.75 s but held by the pedal until it lifts at 1 s.
    private static readonly byte[] DrumAndPedalTrack =
    [
        0x00, 0x99, 0x24, 0x64, 0x81, 0x70, 0x89, 0x24, 0x00, 0x00, 0x99, 0x2A, 0x50, 0x81, 0x70, 0x2A, 0x00,
        0x00, 0xC1, 0x00, 0x00, 0xB1, 0x40, 0x7F, 0x00, 0x91, 0x40, 0x64, 0x81, 0x70, 0x81, 0x40, 0x00, 0x81, 0x70, 0xB1, 0x40, 0x00,
        0x00, 0xFF, 0x2F, 0x00,
    ];

    [Test]
    public async Task AMidiFileBecomesNotesInSecondsOnGeneralMidiInstruments()
    {
        var song = MusicFormats.Read(Midi(1, 480, TempoTrack, BassTrack, DrumAndPedalTrack), "test.mid");
        await Assert.That(Near(song.Length, 2.0)).IsTrue();
        await Assert.That(song.Notes.Count).IsEqualTo(5);
        var byPart = song.Notes.Select(n => (Part: song.Instruments[n.Instrument].Part, Note: n)).ToList();

        var bass = byPart.Where(p => p.Part == ScorePart.Bass).Select(p => p.Note).ToList();
        await Assert.That(bass.Count).IsEqualTo(2);
        await Assert.That(bass[0].Pitch).IsEqualTo(60.0);
        await Assert.That(Near(bass[0].Start, 0) && Near(bass[0].Duration, 0.5)).IsTrue();
        await Assert.That(bass[1].Pitch).IsEqualTo(62.0);
        await Assert.That(Near(bass[1].Start, 0.5) && Near(bass[1].Duration, 1.5)).IsTrue();
        // Loudness: velocity, channel volume (100 by default) and expression, each squared.
        await Assert.That(Math.Abs(bass[0].Velocity - Math.Pow(100 / 127.0, 4))).IsLessThan(1e-6);
        await Assert.That(bass[1].Velocity).IsGreaterThan(bass[0].Velocity);

        var kick = byPart.Single(p => p.Part == ScorePart.Kick).Note;
        await Assert.That(kick.Pitch is null).IsTrue();
        await Assert.That(Near(kick.Start, 0) && Near(kick.Duration, 0.25)).IsTrue();
        var hat = byPart.Single(p => p.Part == ScorePart.Hats).Note;
        await Assert.That(Near(hat.Start, 0.25) && Near(hat.Duration, 0.25)).IsTrue();
        var piano = byPart.Single(p => p.Part == ScorePart.Keys).Note;
        await Assert.That(piano.Pitch).IsEqualTo(64.0);
        await Assert.That(Near(piano.Start, 0.5) && Near(piano.Duration, 0.5)).IsTrue(); // the pedal held it

        var player = new MusicPlayer(song);
        player.Play(loop: false);
        var output = new float[96_000 * 2];
        player.Render(output);
        await Assert.That(output.All(float.IsFinite)).IsTrue();
        await Assert.That(output.Any(s => s != 0)).IsTrue();
        await Assert.That(player.State).IsEqualTo(MusicPlaybackState.Stopped);
    }

    [Test]
    public async Task SmpteTimeIgnoresTempo()
    {
        // 25 frames a second, 40 ticks a frame: 1000 ticks a second. A note from tick 500 to 1500.
        byte[] track = [0x00, 0xFF, 0x51, 0x03, 0x0F, 0x42, 0x40, 0x83, 0x74, 0x90, 0x45, 0x40, 0x87, 0x68, 0x80, 0x45, 0x00, 0x00, 0xFF, 0x2F, 0x00];
        var song = MusicFormats.Read(Midi(0, unchecked((short)0xE728), track));
        await Assert.That(song.Notes.Count).IsEqualTo(1);
        await Assert.That(Near(song.Notes[0].Start, 0.5) && Near(song.Notes[0].Duration, 1)).IsTrue();
        await Assert.That(Near(song.Length, 1.5)).IsTrue();
    }

    [Test]
    public async Task EveryGeneralMidiProgramAndDrumMakesAPlayableInstrument()
    {
        var instruments = Enumerable.Range(0, 128).Select(GeneralMidi.Melodic).Concat(Enumerable.Range(0, 128).Select(GeneralMidi.Drum)).ToList();
        var notes = instruments.Select((_, i) => new Note(i * 0.01, 0.05, i < 128 ? 60 : null, 1, i));
        var song = new Song(instruments, notes, 3, [GeneralMidi.Room]);
        var player = new MusicPlayer(song);
        player.Play(loop: false);
        var output = new float[48_000 * 3 * 2];
        player.Render(output);
        await Assert.That(output.All(float.IsFinite)).IsTrue();
        await Assert.That(GeneralMidi.Melodic(33).Part).IsEqualTo(ScorePart.Bass);
        await Assert.That(GeneralMidi.Melodic(89).Part).IsEqualTo(ScorePart.Pad);
        await Assert.That(GeneralMidi.Drum(38).Part).IsEqualTo(ScorePart.Snare);
        await Assert.That(() => GeneralMidi.Melodic(128)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BrokenMidiFilesAreRefused()
    {
        var good = Midi(1, 480, TempoTrack, BassTrack);
        await Assert.That(() => MusicFormats.Read(good[..^5])).Throws<InvalidDataException>(); // truncated
        await Assert.That(() => MusicFormats.Read(Midi(3, 480, BassTrack))).Throws<InvalidDataException>(); // format 3
        await Assert.That(() => MusicFormats.Read(Midi(1, 0, BassTrack))).Throws<InvalidDataException>(); // no time base
        await Assert.That(() => MusicFormats.Read(Midi(1, 480, TempoTrack))).Throws<InvalidDataException>(); // no notes
        await Assert.That(() => MusicFormats.Read(Midi(0, 480, [0x00, 0x3C, 0x40]))).Throws<InvalidDataException>(); // data without status
    }

    [Test]
    public async Task FormatsAreRecognisedByContent()
    {
        await Assert.That(SongJsonFormat.Instance.CanRead(Encoding.UTF8.GetBytes(MinimalSong))).IsTrue();
        await Assert.That(MidiFormat.Instance.CanRead(Encoding.UTF8.GetBytes(MinimalSong))).IsFalse();
        await Assert.That(MidiFormat.Instance.CanRead(Midi(0, 480, BassTrack))).IsTrue();
        // A byte order mark is fine.
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(MinimalSong)).ToArray();
        await Assert.That(MusicFormats.Read(withBom).Length).IsEqualTo(2.0);
        // Anything else names the formats the player reads.
        var message = "";
        try
        {
            MusicFormats.Read("""{"format":"other"}"""u8, "song.txt");
        }
        catch (InvalidDataException exception)
        {
            message = exception.Message;
        }
        await Assert.That(message.Contains("song.txt", StringComparison.Ordinal)).IsTrue();
        await Assert.That(message.Contains("Standard MIDI File", StringComparison.Ordinal)).IsTrue();
        await Assert.That(() => MusicFormats.Read("not json"u8)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task AFormatOfOnesOwnCanBeTaught()
    {
        var format = new OneNoteFormat();
        var song = MusicFormats.Read("ONE"u8, null, [format, .. MusicFormats.BuiltIn]);
        await Assert.That(song.Notes.Count).IsEqualTo(1);
        await Assert.That(() => MusicFormats.Read("ONE"u8)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task PathAndStreamLoadingReadTheSameSong()
    {
        var directory = Directory.CreateTempSubdirectory("encore-song-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "song.song.json");
            File.WriteAllText(path, MinimalSong);
            var fromPath = new MusicPlayer(path);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("xx" + MinimalSong));
            stream.Position = 2;
            var fromStream = new MusicPlayer(stream);
            await Assert.That(stream.Position).IsEqualTo(stream.Length);
            await Assert.That(fromStream.Song.Notes.SequenceEqual(fromPath.Song.Notes)).IsTrue();
            File.Delete(path); // nothing keeps the file
            await Assert.That(() => new MusicPlayer(Path.Combine(directory.FullName, "missing.song.json"))).Throws<FileNotFoundException>();
            await Assert.That(() => new MusicPlayer(" ")).Throws<ArgumentException>();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string BundledScore(string relative) => TestScenes.RepositoryFile(Path.Combine("assets/audio/scores", relative));

    [Test]
    [Arguments("themes/disco-opus55-b/65-garage-disco-dub-break.song.json")]
    [Arguments("themes/funk-opus55-b/75-afro-funk-market-loop.song.json")]
    [Arguments("themes/funk-opus55-c/81-fusion-crosswind-modal.song.json")]
    [Arguments("examples/first-light.song.json")]
    public async Task TheBundledSongsLoadAndPlay(string relative)
    {
        var player = new MusicPlayer(BundledScore(relative));
        player.Play();
        var output = new float[48_000 * 4 * 2];
        player.Render(output);
        await Assert.That(output.All(float.IsFinite)).IsTrue();
        double sum = 0;
        foreach (var s in output) sum += s * s;
        await Assert.That(Math.Sqrt(sum / output.Length)).IsGreaterThan(0.005).And.IsLessThan(0.5);
        await Assert.That(output.Max(s => MathF.Abs(s))).IsLessThan(2f);
    }

    [Test]
    public async Task AScoreNameFindsWhicheverFormatIsThere()
    {
        var scores = TestScenes.RepositoryFile("assets/audio/scores");
        await Assert.That(MixerEngine.ScoreFile(scores, "examples/first-light").EndsWith("first-light.song.json", StringComparison.Ordinal)).IsTrue();
        await Assert.That(MixerEngine.ScoreFile(scores, "themes/funk-opus55-b/75-afro-funk-market-loop").EndsWith("75-afro-funk-market-loop.song.json", StringComparison.Ordinal)).IsTrue();
        await Assert.That(MixerEngine.ScoreFile(scores, "missing").EndsWith("missing.song.json", StringComparison.Ordinal)).IsTrue();
    }

    private sealed class OneNoteFormat : IMusicFormat
    {
        public string Name => "One note";

        public IReadOnlyList<string> Extensions { get; } = [".one"];

        public bool CanRead(ReadOnlySpan<byte> data) => data.SequenceEqual("ONE"u8);

        public Song Read(ReadOnlySpan<byte> data) => new([new Instrument()], [new Note(0, 0.5, 60, 1, 0)], 1);
    }
}
