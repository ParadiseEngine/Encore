# Encore

A .NET audio engine for games (`Encore.Audio`): a 48 kHz stereo mixer for a table of sound effects, a music
synthesizer that plays songs from several formats, and the device output a host opens to hear them. .NET 11,
NativeAOT-compatible. MIT licensed (see `LICENSE`).

https://github.com/user-attachments/assets/5daa39d0-70ae-4d3e-9738-c28a4a6edb19

Two NuGet packages:

| Package | What it is |
|---|---|
| [Paradise.Encore](https://www.nuget.org/packages/Paradise.Encore) | The engine (assembly and namespace `Encore.Audio`). It depends on nothing but .NET itself: no packages, no native libraries, no unsafe code. It never opens a device; the host pulls samples from `AudioMixer.Render`. |
| [Paradise.Encore.Sdl](https://www.nuget.org/packages/Paradise.Encore.Sdl) | Optional (assembly and namespace `Encore.Audio.Sdl`): `SdlAudioOutput` plays the mixer through SDL3's default playback device. The only package with a dependency (the SDL3 binding). |

```sh
dotnet add package Paradise.Encore
dotnet add package Paradise.Encore.Sdl   # only for the SDL3 device output
```

## What is here

| Path | What it is |
|---|---|
| `src/Encore.Audio/IAudioEngine.cs` | The commands a host posts: emitters that follow a pose, a listener, events on an emitter or at a point, the music, `Commit` once a frame. `AudioPose`. |
| `src/Encore.Audio/Mixer/` | `SoundBank` (loads the sound table `events.json` and the WAV masters it lists), `AudioMixer` (voice pool, effects/ambience/music buses, distance fade and pan from the listener, shared voice pools, music crossfade and arrangement, a −1 dBFS limiter), `MixerEngine` (the `IAudioEngine` over the mixer: variations, detune, loops, song loading), `WavFile`/`WavWriter`. |
| `src/Encore.Audio/Music/` | The music: `Song` (instruments, notes in seconds, effect buses), `MusicPlayer` (plays a song, loops it, arranges its parts), the synthesizer under `Synth/`, and the formats under `Formats/`. |
| `src/Encore.Audio.Sdl/` | The optional device output: `SdlAudioOutput`, SDL3's default playback device at 48 kHz stereo float, whose callback renders the mixer. |
| `src/Encore.Audio/AudioCues.cs` | An `IAudioEngine` wrapper that writes down every event and music change with its time, for checking a mix offline. |
| `src/Encore.Audio/AudioEvents.cs` | The `Play_X` / `Stop_X` / `Pause_X` / `Resume_X` naming of the table's events. |
| `tests/Encore.Audio.AotSmoke/` | The NativeAOT smoke test: a native executable that exercises both packages (see NativeAOT below). |
| `tests/Encore.Audio.Tests/` | Unit tests (TUnit) of the mixer, the synthesizer, the player and the formats, and a check that the core stays free of dependencies; no sound device needed. |
| `assets/audio/scores/` | Three of ENCORE's themes as `encore.song` files with their provenance sidecars, and `examples/first-light.song.json`, a short example written by hand. |
| `docs/audio/` | A rendered sample of one of the themes, linked from Music below. |

## Music

**[▶ Listen: Afro-Funk Market Loop](docs/audio/afro-funk-market-loop.mp3)**: the first 24 seconds of
`assets/audio/scores/themes/funk-opus55-b/75-afro-funk-market-loop.song.json`, rendered offline by
`MusicPlayer` (with a two-second fade-out added) and encoded as a 128 kbit/s MP3.

### The synthesizer

Every note of a song plays on a voice of its `Instrument`, built from sections:

- **source:** an oscillator (sine, triangle, band-limited sawtooth, square or pulse), optionally a detuned and
  spread `Unison` stack and sine `Fm`, with white noise blended in; or a noise (white, pink, brown);
- **filters:** up to four resonant state-variable filters (lowpass, highpass, bandpass; 12 or 24 dB an octave);
- **colour:** soft-clipping drive and a four-stage `Phaser`;
- **amplitude:** an ADSR `Envelope` and constant-power pan;
- **`Sends`:** dry, and shares to the song's effect buses: an eight-line feedback-delay-network reverb and a
  damped feedback delay;
- **`Modulations`:** a matrix of up to eight routes, each an `Lfo` (sine, triangle, square, saw) or an
  `Envelope` moving the pitch (semitones), a filter's cutoff (octaves), the loudness, the pulse width or the
  FM index. Vibrato, tremolo, drum pitch drops, filter sweeps and FM envelopes are all routes.

Voices are pooled (128; the quietest releasing voice, or else the oldest, makes way), rendering runs fixed
64-frame blocks and never allocates, and the same song always renders the same samples: noises and unison
phases come from the song's seed.

### Formats

`MusicFormats.Read` and `MusicFormats.Load` recognise a song by its content, not its file name:

| Format | Reader | What it reads |
|---|---|---|
| `encore.song` JSON (`.song.json`) | `SongJsonFormat` | This engine's own: everything a `Song` holds, times in beats at a tempo. `SongJsonFormat.Write` writes one, so any song (a MIDI file, say) can be converted. The format is described on the class. |
| Standard MIDI File (`.mid`, `.midi`) | `MidiFormat` | Formats 0–2: notes, tempo map or SMPTE time, programs, volume, pan, expression, sustain pedal and reverb send, voiced by the `GeneralMidi` presets (every program family and a drum kit on channel 10), each tagged with the part it plays. |

Another format is a class implementing `IMusicFormat` (`CanRead` its signature, `Read` a `Song`), passed to
`MusicFormats.Read` with the built-in ones. A song can also be built in code:

```csharp
using Encore.Audio;
using Encore.Audio.Music;

var bass = new Instrument
{
    Part = ScorePart.Bass, Waveform = Waveform.Sawtooth, Gain = 0.3,
    Envelope = new Envelope(0.003, 0.2, 0.5, 0.05),
    Filters = [new Filter(FilterMode.Lowpass, 400) { Resonance = 1.5 }],
    Modulations = [Modulation.ByEnvelope(ModulationTarget.Cutoff, 2, Envelope.Hit(0.15))],
};
var song = new Song([bass], [new Note(0, 0.25, 36, 1, 0), new Note(0.5, 0.25, 43, 0.8f, 0)], length: 1);
var player = new MusicPlayer(song);
player.Play(loop: true);
player.Render(buffer); // interleaved stereo floats at 48 kHz
```

### Arrangement

Each instrument names its part (`ScorePart`: kick, snare, hats, bass, keys, pad, melody, or untagged).
`MusicPlayer.Arrange` fades parts in and out; the mixer uses it to keep only a sound table's
`battleMusic` parts during a fight.

## Use

```csharp
using System.Numerics;
using Encore.Audio;
using Encore.Audio.Sdl;                          // only with the optional SDL output

var bank = SoundBank.Load(effectsDirectory);     // events.json and the masters it lists
var mixer = new AudioMixer(bank);
using var output = new SdlAudioOutput(mixer);    // opens the device; its callback renders the mixer
using var engine = new MixerEngine(bank, mixer, MixerEngine.ScoresIn(scoresDirectory));

const ulong listener = 1;
engine.RegisterObject(listener);
engine.SetListener(listener);
engine.SetPosition(listener, AudioPose.LookingAt(eye, target));

// Each frame: post, then commit.
engine.PlayMusic("themes/funk-opus55-b/75-afro-funk-market-loop", fighting: false);
engine.PostEventAt("Play_Horn", new Vector3(2, 0, 0));
engine.Commit();
```

A music name is a path under the scores directory without its ending; `MixerEngine.ScoreFile` takes the
first of `.song.json`, `.mid` and `.midi` that exists.

Without `Encore.Audio.Sdl`, play through any device by calling `mixer.Render(buffer)` from its callback
(interleaved stereo floats at 48 kHz; only that call advances the mix, on whichever thread the device uses), or
render offline and write the result with `WavWriter`. A song
alone plays through `MusicPlayer` (`Play`, `Render`, `Pause`, `Resume`, `Stop`, `Arrange`).

The sound table is a version 1 `encore.sound-table` at 48 kHz: `pools` and `sounds` (each with its
`id`, `event`, `kind` one-shot or loop, `bus`, `positioned`, `volumeDb`, `pitchCents`, `maxVoices`,
`minIntervalSeconds`, `minDistance`, `maxDistance`, `pool` and `files`), and optionally `battleMusic`
(the song parts that stay while `PlayMusic` is asked with `fighting`). Masters are 16-bit PCM WAV at
48 kHz, mono when positioned. `MixerTests` writes a small table to read as an example.

## Build and test

.NET SDK 11.0.100 or a later feature band (`global.json`).

```sh
dotnet build Encore.Audio.slnx -c Release
dotnet test --solution Encore.Audio.slnx -c Release
python3 .github/scripts/pack_packages.py 0.0.0-local artifacts/nuget   # pack both packages and check them
SDL_AUDIO_DRIVER=dummy python3 .github/scripts/aot_smoke.py            # publish with NativeAOT and run it
```

### NativeAOT

Both libraries are NativeAOT- and trim-compatible (`IsAotCompatible`, `IsTrimmable`): no reflection, no
runtime code generation, JSON read with `Utf8JsonReader`/`JsonDocument`. The analyzers run on every build, and
warnings are errors. `tests/Encore.Audio.AotSmoke` goes further. CI publishes it with `PublishAot` on Linux,
Windows and macOS, fails on any trim or AOT warning, and runs the native executable. That run loads a song and
a MIDI file, renders music, runs the mixer over a sound table, and lets SDL's native callback pull the mixer
through the dummy audio driver.

## Releasing

Push a tag `vX.Y.Z` (or `vX.Y.Z-beta.1`), or run the **Publish NuGet packages** workflow by hand with a version.
It runs the tests, packs `Paradise.Encore` and `Paradise.Encore.Sdl` at that version, checks them (the core has no
dependencies, the SDL package depends on the core at the same version, a consumer of both builds), and pushes both
to nuget.org with their symbol packages.

Publishing uses nuget.org trusted publishing, with no API key stored in the repository: on nuget.org, add a trusted
publishing policy for the repository `ParadiseEngine/Encore` and the workflow `publish-nuget.yml`. The workflow signs
in as the `NUGET_USER` repository variable, or as the repository owner when it is not set.
