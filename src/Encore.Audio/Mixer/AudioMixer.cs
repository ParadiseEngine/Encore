using System.Numerics;

namespace Encore.Audio;

/// <summary>
/// The game's mix at 48 kHz stereo, rendered on the audio device's thread or offline: a fixed pool of
/// voices playing the <see cref="SoundBank"/>'s clips, positioned ones panned and faded by where they
/// stand from the listener, the one-shots of a table's <see cref="VoicePool"/> sharing its budget (the
/// quietest as heard making way for a louder one); the music's song (<see cref="MusicPlayer"/>), crossfading to the next
/// and arranged for a fight as the table's <see cref="BattleMusic"/> says (its rhythm parts only);
/// and a peak limiter on the master. <see cref="MixerEngine"/> hands it commands in batches through
/// <see cref="Inbox"/>; only <see cref="Render"/> applies them, so all other state belongs to the rendering thread.
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = WavFile.SampleRate;
    public const int Channels = 2;
    /// <summary>Frames per block: positions, fades and gains ramp across one.</summary>
    private const int Block = 128;
    private const float LoopFadeIn = .15f, LoopFadeOut = .3f, AmbienceFade = 2f, PauseFade = .08f, StealFade = .01f, MusicFade = 2.5f;
    /// <summary>Slots past the cap, where the voices that make way for new ones fade out.</summary>
    private const int Spare = 16;
    /// <summary>How far off centre a positioned sound pans at most (1 is one side only).</summary>
    private const float MaxPan = .8f;
    /// <summary>The master's ceiling, −1 dBFS, and the limiter's release time.</summary>
    private const float Ceiling = .891f, LimiterRelease = .15f;
    private static readonly float Centre = MathF.Sqrt(.5f);

    private readonly Voice[] _voices;
    private readonly int _cap;
    private readonly long[] _lastStart;
    /// <summary>When each of the table's <see cref="SoundBank.Pools"/> last started a voice.</summary>
    private readonly long[] _poolStart;
    private readonly Dictionary<ulong, Vector3> _emitters = [];
    private readonly float[] _left = new float[Block], _right = new float[Block], _music = new float[Block * Channels];
    private readonly float[] _buses = new float[3];
    private readonly float _limiterRelease = 1 - MathF.Exp(-1 / (LimiterRelease * SampleRate));
    private List<MixCommand> _applying = [];
    private Vector3 _listener, _listenerRight = Vector3.UnitX;
    private MusicVoice? _playing, _leaving;
    private float _limiter = 1;
    private long _clock;
    private float _peak;
    private readonly BattleMusic? _battle;
    /// <summary>Whether a fight's arrangement is asked for, and the parts' gains it gives (scratch).</summary>
    private bool _fighting;
    private readonly float[] _arrangement = new float[ScoreParts.Count];

    /// <param name="bank">The sounds its voices play; the engine's commands name them.</param>
    /// <param name="voices">The most sounds playing at once; past it a new one fades the oldest one-shot (else loop) out to take its place.</param>
    public AudioMixer(SoundBank bank, int voices = 64)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentOutOfRangeException.ThrowIfLessThan(voices, 1);
        _cap = voices;
        _voices = new Voice[voices + Spare];
        for (var i = 0; i < _voices.Length; i++) _voices[i] = new Voice();
        _lastStart = new long[bank.Sounds.Count];
        Array.Fill(_lastStart, long.MinValue / 2);
        _poolStart = new long[bank.Pools.Count];
        Array.Fill(_poolStart, long.MinValue / 2);
        _battle = bank.Battle;
        SetBus(AudioBus.Effects, 1);
        SetBus(AudioBus.Ambience, 1);
        SetBus(AudioBus.Music, MusicGain);
    }

    /// <summary>
    /// The music bus's volume, +7 dB: the scores are written quiet (their commissions cap source gains
    /// at 0.55), so this lifts them toward −24 LUFS whole; a fight plays
    /// only their rhythm parts, quieter (the table's <see cref="BattleMusic"/>, design.md 戰鬥只留下節奏組);
    /// a sparse score stays quieter than a busy one, as written.
    /// </summary>
    public const float MusicGain = 2.24f;

    /// <summary>The loudest a bus may be set: +12 dB.</summary>
    public const float MaxBusGain = 4;

    internal MixerInbox Inbox { get; } = new();

    /// <summary>Frames rendered so far.</summary>
    public long RenderedFrames => Interlocked.Read(ref _clock);

    /// <summary>The loudest sample written so far, after the limiter.</summary>
    public float Peak => Volatile.Read(ref _peak);

    /// <summary>Voices sounding now (for checks; read on the rendering thread).</summary>
    internal int ActiveVoices => _voices.Count(v => v.Active);

    /// <summary>A part's gain in the score playing now, 1 with none (for checks; read on the rendering thread).</summary>
    internal float PartGain(ScorePart part) => _playing?.Music.PartGain(part) ?? 1f;

    /// <summary>A part's gain in the score fading out under the one playing, 1 with none (for checks; read on the rendering thread).</summary>
    internal float LeavingPartGain(ScorePart part) => _leaving?.Music.PartGain(part) ?? 1f;

    /// <summary>Whether a fight's arrangement is asked for (for checks; read on the rendering thread).</summary>
    internal bool Fighting => _fighting;

    /// <summary>Sets <paramref name="bus"/>'s volume, 0 to <see cref="MaxBusGain"/>; call it before rendering starts.</summary>
    public void SetBus(AudioBus bus, float gain)
    {
        if (!float.IsFinite(gain) || gain is < 0 or > MaxBusGain) throw new ArgumentOutOfRangeException(nameof(gain));
        _buses[(int)bus] = gain;
    }

    /// <summary>Applies the commands handed over so far, then overwrites <paramref name="interleaved"/> with whole stereo frames of the mix.</summary>
    public void Render(Span<float> interleaved)
    {
        if (interleaved.Length % Channels != 0) throw new ArgumentException("The buffer must contain whole stereo frames.", nameof(interleaved));
        Inbox.Take(ref _applying);
        foreach (var command in _applying) Apply(command);
        _applying.Clear();
        var peak = _peak;
        while (!interleaved.IsEmpty)
        {
            var frames = Math.Min(Block, interleaved.Length / Channels);
            var left = _left.AsSpan(0, frames);
            var right = _right.AsSpan(0, frames);
            left.Clear();
            right.Clear();
            foreach (var voice in _voices)
                if (voice.Active) Mix(voice, left, right);
            MixMusic(left, right);
            for (var i = 0; i < frames; i++)
            {
                var l = left[i];
                var r = right[i];
                var loudest = MathF.Max(MathF.Abs(l), MathF.Abs(r));
                if (loudest * _limiter > Ceiling) _limiter = Ceiling / loudest;
                l *= _limiter;
                r *= _limiter;
                _limiter += (1 - _limiter) * _limiterRelease;
                interleaved[2 * i] = l;
                interleaved[2 * i + 1] = r;
                peak = MathF.Max(peak, MathF.Max(MathF.Abs(l), MathF.Abs(r)));
            }
            interleaved = interleaved[(Channels * frames)..];
            Interlocked.Add(ref _clock, frames);
        }
        Volatile.Write(ref _peak, peak);
    }

    private void Apply(in MixCommand command)
    {
        switch (command.Op)
        {
            case MixOp.Listener:
                _listener = command.Position;
                _listenerRight = command.Right;
                break;
            case MixOp.Emitter:
                _emitters[command.Id] = command.Position;
                break;
            case MixOp.Forget:
                _emitters.Remove(command.Id);
                foreach (var voice in _voices)
                {
                    if (!voice.Active || voice.Emitter != command.Id) continue;
                    // Its one-shots play out where it last stood; its loops fade.
                    voice.Emitter = 0;
                    if (voice.Loop) voice.FadeTo(0, FadeOut(voice.Sound), release: true);
                }
                break;
            case MixOp.Play:
                Start(command);
                break;
            case MixOp.Stop:
                foreach (var voice in _voices)
                    if (voice.Active && voice.Instance == command.Id) voice.FadeTo(0, FadeOut(voice.Sound), release: true);
                break;
            case MixOp.Pause:
                foreach (var voice in _voices)
                    if (voice.Active && voice.Instance == command.Id && !voice.Release)
                    {
                        voice.FadeTo(0, PauseFade, release: false);
                        voice.Paused = true;
                    }
                break;
            case MixOp.Resume:
                foreach (var voice in _voices)
                    if (voice.Active && voice.Instance == command.Id && voice.Paused)
                    {
                        voice.Paused = false;
                        voice.FadeTo(1, PauseFade, release: false);
                    }
                break;
            case MixOp.Music:
                // A third score cuts the one already leaving; the playing one fades out under the new one.
                _leaving = null;
                if (_playing is { } playing)
                {
                    playing.Step = -1f / (MusicFade * SampleRate);
                    _leaving = playing;
                }
                _playing = null;
                if (command.Music is { } music)
                {
                    music.Play(loop: true);
                    // A score that comes in during a fight starts as the fight arranges it.
                    Arrange(music, instantly: true);
                    _playing = new MusicVoice(music) { Step = 1f / (MusicFade * SampleRate) };
                }
                break;
            case MixOp.Arrange:
                _fighting = command.Fighting;
                // A score on its way out never comes back whole: a fight ending with silence (a knock-down)
                // or with another score leaves it as the fight arranged it while it fades. A fight starting
                // takes the parts out of whatever still sounds.
                if (_playing is { } arranged && (command.Fighting || command.Staying)) Arrange(arranged.Music, instantly: false);
                if (_leaving is { } leaving && command.Fighting) Arrange(leaving.Music, instantly: false);
                break;
            default:
                throw new InvalidOperationException($"Unknown mixer command {command.Op}.");
        }
    }

    private void Start(in MixCommand command)
    {
        var sound = command.Sound!;
        if (!sound.Loop && _clock - _lastStart[sound.Index] < sound.MinIntervalFrames) return;
        var positioned = command.Positioned && sound.Positioned;
        var position = command.Emitter != 0 && _emitters.TryGetValue(command.Emitter, out var at) ? at : command.Position;
        if (sound.Pool is { } pool && !Admit(pool, Heard(sound, positioned, position))) return;
        Voice? oldest = null;
        var count = 0;
        foreach (var voice in _voices)
        {
            if (!voice.Active || voice.Sound != sound || voice.Release) continue;
            count++;
            if (oldest is null || voice.Started < oldest.Started) oldest = voice;
        }
        if (count >= sound.MaxVoices) oldest!.FadeTo(0, StealFade, release: true);
        var free = Free();
        free.Active = true;
        free.Sound = sound;
        free.Clip = sound.Clips[command.Clip];
        free.Instance = command.Id;
        free.Emitter = command.Emitter;
        free.Positioned = positioned;
        free.Position = position;
        free.Phase = 0;
        free.Rate = command.Rate;
        free.Loop = sound.Loop;
        free.Paused = false;
        free.Release = false;
        free.Started = _clock;
        if (sound.Loop)
        {
            free.Fade = 0;
            free.FadeTo(1, sound.Bus == AudioBus.Ambience ? AmbienceFade : LoopFadeIn, release: false);
        }
        else
        {
            free.Fade = 1;
            free.FadeTarget = 1;
            free.FadeStep = 0;
        }
        (free.Left, free.Right) = Gains(free);
        _lastStart[sound.Index] = _clock;
        if (sound.Pool is { } shared) _poolStart[shared.Index] = _clock;
    }

    /// <summary>
    /// Whether a one-shot of <paramref name="pool"/>, <paramref name="heard"/> as loud, may start: not
    /// within the pool's interval of its last start, and, with all the pool's voices sounding, only
    /// louder than the quietest of them, which then fades out to make way, so that a crowd's nearest
    /// machines are the ones heard.
    /// </summary>
    private bool Admit(VoicePool pool, float heard)
    {
        if (_clock - _poolStart[pool.Index] < pool.MinIntervalFrames) return false;
        Voice? quietest = null;
        var quietestLevel = 0f;
        var sounding = 0;
        foreach (var voice in _voices)
        {
            if (!voice.Active || voice.Release || !ReferenceEquals(voice.Sound.Pool, pool)) continue;
            sounding++;
            var level = Heard(voice.Sound, voice.Positioned, voice.Position) * voice.Fade;
            if (quietest is null || level < quietestLevel) (quietest, quietestLevel) = (voice, level);
        }
        if (sounding < pool.MaxVoices) return true;
        if (heard <= quietestLevel) return false;
        quietest!.FadeTo(0, StealFade, release: true);
        return true;
    }

    /// <summary>
    /// A slot for a new sound. Past the cap the oldest one-shot sounding, else the oldest loop, fades
    /// out over <see cref="StealFade"/> (cutting it would click) while the new one takes an idle slot;
    /// with none idle, the quietest voice already fading out gives its slot up.
    /// </summary>
    private Voice Free()
    {
        Voice? idle = null, fading = null, oneShot = null, loop = null;
        var sounding = 0;
        foreach (var voice in _voices)
        {
            if (!voice.Active) idle ??= voice;
            else if (voice.Release) { if (fading is null || voice.Fade < fading.Fade) fading = voice; }
            else
            {
                sounding++;
                if (voice.Loop) { if (loop is null || voice.Started < loop.Started) loop = voice; }
                else if (oneShot is null || voice.Started < oneShot.Started) oneShot = voice;
            }
        }
        var oldest = oneShot ?? loop;
        if (sounding >= _cap) oldest!.FadeTo(0, StealFade, release: true);
        return idle ?? fading ?? oldest!;
    }

    private static float FadeOut(SoundSpec sound) => sound.Bus == AudioBus.Ambience ? AmbienceFade : LoopFadeOut;

    /// <summary>
    /// How loud <paramref name="sound"/> is heard standing at <paramref name="position"/> (anywhere,
    /// unpositioned): its volume and its bus's, faded by its distance from the listener.
    /// </summary>
    private float Heard(SoundSpec sound, bool positioned, Vector3 position)
    {
        var gain = sound.Gain * _buses[(int)sound.Bus];
        if (!positioned) return gain;
        var far = Math.Clamp((Vector3.Distance(position, _listener) - sound.MinDistance) / (sound.MaxDistance - sound.MinDistance), 0, 1);
        return gain * (1 - far) * (1 - far);
    }

    /// <summary>The voice's left and right gains now: how loud it is heard, panned by where it stands from the listener.</summary>
    private (float Left, float Right) Gains(Voice voice)
    {
        if (voice.Positioned && voice.Emitter != 0 && _emitters.TryGetValue(voice.Emitter, out var at)) voice.Position = at;
        var gain = Heard(voice.Sound, voice.Positioned, voice.Position);
        if (!voice.Positioned) return voice.Clip.Channels == 2 ? (gain, gain) : (gain * Centre, gain * Centre);
        var offset = voice.Position - _listener;
        var distance = offset.Length();
        // Off to the side pans, the less the nearer: a sound at the listener is in the middle.
        var pan = distance > 1e-3f ? Vector3.Dot(offset / distance, _listenerRight) * MathF.Min(1, distance / voice.Sound.MinDistance) * MaxPan : 0;
        var angle = (pan + 1) * MathF.PI / 4;
        return (gain * MathF.Cos(angle), gain * MathF.Sin(angle));
    }

    private void Mix(Voice voice, Span<float> left, Span<float> right)
    {
        var (toLeft, toRight) = Gains(voice);
        if (voice.Paused && voice.Fade <= 0)
        {
            // Stopped or forgotten after its pause had silenced it, it has nothing left to fade.
            if (voice.Release) voice.Active = false;
            (voice.Left, voice.Right) = (toLeft, toRight);
            return;
        }
        var samples = voice.Clip.Samples;
        var channels = voice.Clip.Channels;
        var length = voice.Clip.Frames;
        var frames = left.Length;
        var stepLeft = (toLeft - voice.Left) / frames;
        var stepRight = (toRight - voice.Right) / frames;
        var gainLeft = voice.Left;
        var gainRight = voice.Right;
        const float Scale = 1f / 32768;
        for (var i = 0; i < frames; i++)
        {
            gainLeft += stepLeft;
            gainRight += stepRight;
            var fade = voice.Fade;
            if (voice.FadeStep != 0)
            {
                fade += voice.FadeStep;
                if (voice.FadeStep > 0 ? fade >= voice.FadeTarget : fade <= voice.FadeTarget)
                {
                    fade = voice.FadeTarget;
                    voice.FadeStep = 0;
                }
                voice.Fade = fade;
            }
            var index = (int)voice.Phase;
            var fraction = (float)(voice.Phase - index);
            var next = index + 1 < length ? index + 1 : voice.Loop ? 0 : index;
            if (channels == 1)
            {
                var a = samples[index];
                var sample = (a + (samples[next] - a) * fraction) * Scale * fade;
                left[i] += sample * gainLeft;
                right[i] += sample * gainRight;
            }
            else
            {
                var l = samples[2 * index];
                var r = samples[2 * index + 1];
                left[i] += (l + (samples[2 * next] - l) * fraction) * Scale * fade * gainLeft;
                right[i] += (r + (samples[2 * next + 1] - r) * fraction) * Scale * fade * gainRight;
            }
            if (voice.Release && fade <= 0 || voice.Paused && fade <= 0)
            {
                if (voice.Release) voice.Active = false;
                break;
            }
            voice.Phase += voice.Rate;
            if (voice.Phase >= length)
            {
                if (!voice.Loop)
                {
                    voice.Active = false;
                    break;
                }
                voice.Phase -= length;
            }
        }
        (voice.Left, voice.Right) = (toLeft, toRight);
    }

    /// <summary>
    /// Arranges <paramref name="music"/> for the fight or out of it (the table's <see cref="BattleMusic"/>): at once, or
    /// leaving over its leave time as a fight starts and coming back over its return time as it ends. A table without
    /// one leaves every score whole.
    /// </summary>
    private void Arrange(MusicPlayer music, bool instantly)
    {
        if (_battle is not { } battle) return;
        battle.Gains(_fighting, _arrangement);
        music.Arrange(_arrangement, instantly ? 0 : _fighting ? battle.LeaveSeconds : battle.ReturnSeconds);
    }

    private void MixMusic(Span<float> left, Span<float> right)
    {
        var bus = _buses[(int)AudioBus.Music];
        if (MixMusic(_playing, left, right, bus) is false) _playing = null;
        if (MixMusic(_leaving, left, right, bus) is false) _leaving = null;
    }

    /// <summary>Adds <paramref name="voice"/>'s score under its equal-power fade; false once it has faded out, null without one.</summary>
    private bool? MixMusic(MusicVoice? voice, Span<float> left, Span<float> right, float bus)
    {
        if (voice is null) return null;
        var buffer = _music.AsSpan(0, left.Length * Channels);
        voice.Music.Render(buffer);
        for (var i = 0; i < left.Length; i++)
        {
            voice.Fade = Math.Clamp(voice.Fade + voice.Step, 0, 1);
            var gain = MathF.Sin(voice.Fade * MathF.PI / 2) * bus;
            left[i] += buffer[2 * i] * gain;
            right[i] += buffer[2 * i + 1] * gain;
        }
        return voice.Step >= 0 || voice.Fade > 0;
    }

    private sealed class Voice
    {
        public bool Active;
        public SoundSpec Sound = null!;
        public SoundClip Clip = null!;
        /// <summary>The engine's id for a loop it may stop, pause or resume; 0 for a one-shot.</summary>
        public ulong Instance;
        /// <summary>The emitter it follows; 0 once it plays where it stands.</summary>
        public ulong Emitter;
        public bool Positioned;
        public Vector3 Position;
        public double Phase;
        public double Rate;
        public bool Loop;
        public float Fade, FadeTarget, FadeStep;
        /// <summary>Ends when its fade reaches 0 (a stop); otherwise a fade to 0 holds it (a pause).</summary>
        public bool Release;
        public bool Paused;
        public long Started;
        /// <summary>The gains the last block ended on.</summary>
        public float Left, Right;

        public void FadeTo(float target, float seconds, bool release)
        {
            FadeTarget = target;
            Release |= release;
            var distance = target - Fade;
            FadeStep = distance == 0 ? 0 : distance / MathF.Max(1, seconds * SampleRate);
        }
    }

    private sealed class MusicVoice(MusicPlayer music)
    {
        public readonly MusicPlayer Music = music;
        public float Fade;
        public float Step;
    }
}

internal enum MixOp : byte { Listener, Emitter, Forget, Play, Stop, Pause, Resume, Music, Arrange }

/// <summary>One thing the engine asks of the mixer; which fields it uses depends on <see cref="Op"/>.</summary>
internal struct MixCommand
{
    public MixOp Op;
    /// <summary>The emitter (Emitter, Forget) or the loop's instance (Play, Stop, Pause, Resume).</summary>
    public ulong Id;
    /// <summary>The emitter a play follows, or 0 for one standing at <see cref="Position"/>.</summary>
    public ulong Emitter;
    public Vector3 Position;
    /// <summary>The listener's right hand.</summary>
    public Vector3 Right;
    public SoundSpec? Sound;
    public int Clip;
    public double Rate;
    public bool Positioned;
    public MusicPlayer? Music;
    /// <summary>Whether a fight's arrangement is asked for (Arrange).</summary>
    public bool Fighting;
    /// <summary>Whether the score playing goes on playing, rather than giving way to another or to silence (Arrange).</summary>
    public bool Staying;
}

/// <summary>Where the engine's batches wait for the mixer: a list swapped under a lock held only to swap or append.</summary>
internal sealed class MixerInbox
{
    private readonly Lock _lock = new();
    private List<MixCommand> _pending = [];

    /// <summary>Host thread: appends a batch.</summary>
    public void Post(List<MixCommand> batch)
    {
        lock (_lock) _pending.AddRange(batch);
    }

    /// <summary>Mixer thread: takes everything posted, handing back <paramref name="spare"/> (empty) to fill next.</summary>
    public void Take(ref List<MixCommand> spare)
    {
        lock (_lock) (_pending, spare) = (spare, _pending);
    }
}
