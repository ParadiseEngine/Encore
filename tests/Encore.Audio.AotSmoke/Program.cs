// A NativeAOT smoke test: published with PublishAot=true and run as a native executable, it exercises both
// packages the way a game does. It exits 0 when everything worked and prints what failed otherwise.
// The SDL output is checked when SDL_AUDIO_DRIVER is set (CI uses the "dummy" driver, which needs no device).
using System.Numerics;
using System.Text;
using Encore.Audio;
using Encore.Audio.Music;
using Encore.Audio.Music.Formats;
using Encore.Audio.Sdl;

var failures = new List<string>();

void Check(string what, bool ok)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures.Add(what);
}

static (double Rms, bool Finite) Measure(ReadOnlySpan<float> samples)
{
    double sum = 0;
    var finite = true;
    foreach (var s in samples)
    {
        finite &= float.IsFinite(s);
        sum += s * s;
    }
    return (Math.Sqrt(sum / Math.Max(1, samples.Length)), finite);
}

// 1. The native song format: read, play, write and read back.
const string SongJson = """
    {
      "format": "encore.song", "version": 1, "bpm": 120, "length": 4,
      "effects": [ { "delayBeats": 0.5 } ],
      "instruments": [
        { "part": "kick", "wave": "sine", "gain": 0.6, "defaultPitch": 33,
          "modulations": [ { "target": "pitch", "amount": 24, "envelope": { "attack": 0, "decay": 0.04, "sustain": 0 } } ] },
        { "part": "pad", "wave": "sawtooth", "gain": 0.1, "unison": { "voices": 5, "detune": 0.2, "spread": 0.7 },
          "filters": [ { "mode": "lowpass", "cutoff": 1500 } ], "phaser": { "rate": 0.3, "depth": 0.5, "center": 1000, "sweep": 600 },
          "modulations": [ { "target": "cutoff", "filter": 0, "amount": 1, "lfo": { "rate": 0.5, "shape": "triangle" } } ],
          "sends": { "bus": 0, "reverb": 0.3, "delay": 0.2 } },
        { "part": "hats", "wave": "white", "gain": 0.1, "filters": [ { "mode": "highpass", "cutoff": 7000 } ],
          "envelope": { "attack": 0.001, "decay": 0.05, "sustain": 0 } }
      ],
      "notes": [ [0, 1, 0], [1, 1, 0], [0, 4, 1, 57], [0, 4, 1, 64], [0.5, 0.25, 2], [1.5, 0.25, 2, null, 0.6] ]
    }
    """;
var song = MusicFormats.Read(Encoding.UTF8.GetBytes(SongJson), "smoke.song.json");
var player = new MusicPlayer(song);
player.Play();
var music = new float[MusicPlayer.SampleRate * 2];
player.Render(music);
var (musicRms, musicFinite) = Measure(music);
Check($"encore.song plays (rms {musicRms:0.0000})", musicFinite && musicRms > 0.001);
var written = SongJsonFormat.Write(song);
Check("encore.song round-trips", SongJsonFormat.Write(MusicFormats.Read(Encoding.UTF8.GetBytes(written))) == written);

// 2. A Standard MIDI file: a program change, a bass note and a kick on the drum channel.
byte[] track = [0x00, 0xC0, 0x21, 0x00, 0x90, 0x28, 0x64, 0x00, 0x99, 0x24, 0x64, 0x83, 0x60, 0x80, 0x28, 0x40, 0x00, 0x89, 0x24, 0x40, 0x00, 0xFF, 0x2F, 0x00];
byte[] midi = [.. "MThd"u8, 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0, .. "MTrk"u8, 0, 0, 0, (byte)track.Length, .. track];
var midiSong = MusicFormats.Read(midi, "smoke.mid");
var midiPlayer = new MusicPlayer(midiSong);
midiPlayer.Play(loop: false);
var midiOut = new float[MusicPlayer.SampleRate];
midiPlayer.Render(midiOut);
var (midiRms, midiFinite) = Measure(midiOut);
Check($"MIDI plays ({midiSong.Notes.Count} notes, rms {midiRms:0.0000})", midiSong.Notes.Count == 2 && midiFinite && midiRms > 0.001);

// 3. The mixer and engine over an in-memory sound table, with the song as the level's music.
byte[] Tone(float hertz)
{
    using var stream = new MemoryStream();
    using (var writer = new WavWriter(stream, 1))
    {
        var samples = new float[AudioMixer.SampleRate / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * hertz * i / AudioMixer.SampleRate);
        writer.Append(samples);
    }
    return stream.ToArray();
}
const string Table = """
    {
      "schemaVersion": 1, "kind": "encore.sound-table", "sampleRate": 48000, "generator": "smoke",
      "pools": [],
      "sounds": [
        { "id": "tone", "event": "Play_Tone", "kind": "one-shot", "bus": "effects", "positioned": true, "volumeDb": 0, "pitchCents": 0,
          "maxVoices": 4, "minIntervalSeconds": 0, "minDistance": 2, "maxDistance": 20, "pool": "", "files": ["tone.wav"] }
      ],
      "battleMusic": { "parts": ["kick"], "volumeDb": -6, "leaveSeconds": 1, "returnSeconds": 1 }
    }
    """;
var bank = SoundBank.Load(Encoding.UTF8.GetBytes(Table), file => Tone(440), "smoke/events.json");
var mixer = new AudioMixer(bank);
using (var engine = new MixerEngine(bank, mixer, name => Task.FromResult(new MusicPlayer(song))))
{
    const ulong Listener = 1;
    engine.RegisterObject(Listener);
    engine.SetListener(Listener);
    engine.SetPosition(Listener, AudioPose.LookingAt(Vector3.Zero, -Vector3.UnitZ));
    engine.PlayMusic("smoke", fighting: true);
    engine.PostEventAt("Play_Tone", new Vector3(3, 0, 0));
    engine.Commit();
    var mix = new float[AudioMixer.SampleRate * AudioMixer.Channels];
    mixer.Render(mix);
    var (mixRms, mixFinite) = Measure(mix);
    Check($"mixer renders effects and music (rms {mixRms:0.0000}, peak {mixer.Peak:0.000})", mixFinite && mixRms > 0.001 && mixer.Peak <= 1f);
}

// 4. The SDL output: its native callback renders the mixer.
if (Environment.GetEnvironmentVariable("SDL_AUDIO_DRIVER") is { Length: > 0 } driver)
{
    try
    {
        var silentMixer = new AudioMixer(bank);
        using var output = new SdlAudioOutput(silentMixer);
        Thread.Sleep(500);
        output.ThrowIfFailed();
        Check($"SDL output pulls the mixer through its native callback ({driver} driver, {output.Seconds:0.00} s)", output.Seconds > 0);
    }
    catch (Exception error)
    {
        Check($"SDL output ({driver} driver): {error.Message}", false);
    }
}
else Console.WriteLine("skip SDL output (set SDL_AUDIO_DRIVER, e.g. dummy, to check it)");

Console.WriteLine(failures.Count == 0 ? "NativeAOT smoke test passed." : $"{failures.Count} check(s) failed.");
return failures.Count == 0 ? 0 : 1;
