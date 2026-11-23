using System.Numerics;

namespace Encore.Audio;

/// <summary>
/// The game's <see cref="IAudioEngine"/>: on the host thread it turns events into the
/// <see cref="AudioMixer"/>'s commands, picking each one-shot's variation (never the one just played
/// when it has others) and detune at random, keeping each emitter's loops so that a <c>Stop_X</c>,
/// <c>Pause_X</c> or <c>Resume_X</c> finds the voices its <c>Play_X</c> started, and loading the
/// music's scores through its loader (off the host thread in an interactive native run).
/// <see cref="Commit"/> hands a frame's commands over at once.
/// </summary>
public sealed class MixerEngine : IAudioEngine
{
    private readonly SoundBank _bank;
    private readonly AudioMixer _mixer;
    private readonly Func<string, Task<MusicPlayer>> _scores;
    private readonly string? _fixedScore;
    private readonly Random _random;
    private readonly int[] _lastClip;
    private readonly List<MixCommand> _staged = [];
    private readonly Dictionary<ulong, Emitter> _emitters = [];
    private ulong _listener;
    private ulong _nextInstance = 1;
    /// <summary>The score asked for last, and the one the mixer was handed last (null: silence).</summary>
    private string? _music, _heard;
    /// <summary>Whether a fight's arrangement was asked for last.</summary>
    private bool _fighting;
    /// <summary>Whether the score the mixer plays was left as the fight arranged it when the fight ended, because another was on its way.</summary>
    private bool _leftArranged;
    private (string Score, Task<MusicPlayer> Load)? _loading;
    private bool _disposed;

    /// <param name="scores">
    /// Loads a score by its name (themes/…): <see cref="ScoresIn"/> a directory, or from the files a
    /// page downloaded. A load that has already completed is heard at once.
    /// </param>
    /// <param name="fixedScore">A score file played throughout instead of the levels' music (the host's <c>--score</c>).</param>
    /// <param name="seed">Seeds the variations' and detunes' choice.</param>
    public MixerEngine(SoundBank bank, AudioMixer mixer, Func<string, Task<MusicPlayer>> scores, string? fixedScore = null, int seed = 1)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(scores);
        _bank = bank;
        _mixer = mixer;
        _scores = scores;
        _fixedScore = fixedScore;
        _random = new Random(seed);
        _lastClip = new int[bank.Sounds.Count];
        Array.Fill(_lastClip, -1);
        if (fixedScore is not null) Stage(new MixCommand { Op = MixOp.Music, Music = new MusicPlayer(fixedScore) });
    }

    /// <summary>The scores' root beside the executable that Encore.App ships.</summary>
    public static string DefaultScores => Path.Combine(AppContext.BaseDirectory, "assets", "audio", "scores");

    /// <summary>The endings a score's file may have, in the order <see cref="ScoreFile"/> looks for them: this engine's songs, then MIDI files.</summary>
    public static IReadOnlyList<string> ScoreExtensions { get; } = [".song.json", ".mid", ".midi"];

    /// <summary>
    /// The file a score name stands for under <paramref name="scores"/>: the first of its <see cref="ScoreExtensions"/>
    /// that exists (the first ending when none does, for the error to name).
    /// </summary>
    public static string ScoreFile(string scores, string score)
    {
        foreach (var extension in ScoreExtensions)
        {
            var file = Path.Combine(scores, score + extension);
            if (File.Exists(file)) return file;
        }
        return Path.Combine(scores, score + ScoreExtensions[0]);
    }

    /// <summary>
    /// The scores under <paramref name="directory"/> (<see cref="ScoreFile"/>), each loaded off the host
    /// thread, so the music playing goes on until the next has loaded; or, <paramref name="inline"/>, on
    /// the host thread before <see cref="PlayMusic"/> returns, for offline renders that must not depend on timing.
    /// </summary>
    public static Func<string, Task<MusicPlayer>> ScoresIn(string directory, bool inline = false) => inline
        ? score => Task.FromResult(new MusicPlayer(ScoreFile(directory, score)))
        : score =>
        {
            var file = ScoreFile(directory, score);
            return Task.Run(() => new MusicPlayer(file));
        };

    /// <summary>Whether a score is loading off the host thread (for checks).</summary>
    internal bool LoadingMusic => _loading is not null;

    public void RegisterObject(ulong id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_emitters.TryAdd(id, new Emitter())) throw new InvalidOperationException($"Audio object {id} is already registered.");
    }

    public void UnregisterObject(ulong id)
    {
        Require(id);
        _emitters.Remove(id);
        if (id == _listener) _listener = 0;
        // The mixer fades the emitter's loops and lets its one-shots play out where it stood.
        Stage(new MixCommand { Op = MixOp.Forget, Id = id });
    }

    public void SetListener(ulong id)
    {
        Require(id);
        _listener = id;
    }

    public void SetPosition(ulong id, AudioPose pose)
    {
        var emitter = Require(id);
        if (!AudioPose.Finite(pose.Position)) throw new ArgumentException("An audio pose needs a finite position.", nameof(pose));
        emitter.Positioned = true;
        Stage(id == _listener
            ? new MixCommand { Op = MixOp.Listener, Position = pose.Position, Right = pose.Right }
            : new MixCommand { Op = MixOp.Emitter, Id = id, Position = pose.Position });
    }

    public void PostEvent(string name, ulong id)
    {
        var emitter = Require(id);
        var listener = id == _listener;
        if (!listener && !emitter.Positioned) throw new InvalidOperationException($"Audio object {id} must be positioned before it plays.");
        if (AudioEvents.Controlled(name) is { } play)
        {
            if (_bank.Of(play) is null) throw new KeyNotFoundException($"Unknown sound event '{name}'.");
            if (emitter.Loops.TryGetValue(play, out var loops))
            {
                var op = name.StartsWith("Stop_", StringComparison.Ordinal) ? MixOp.Stop
                    : name.StartsWith("Pause_", StringComparison.Ordinal) ? MixOp.Pause : MixOp.Resume;
                foreach (var (_, instance) in loops) Stage(new MixCommand { Op = op, Id = instance });
                if (op == MixOp.Stop) emitter.Loops.Remove(play);
            }
            // The control event's own sounds, if the table gives it any (the record winding down).
            if (_bank.Of(name) is { } own) Play(name, own, emitter, id, Vector3.Zero, !listener);
            return;
        }
        // On the listener a sound plays unpositioned, but it still belongs to it: unregistering it fades its loops.
        Play(name, _bank.Of(name) ?? throw new KeyNotFoundException($"Unknown sound event '{name}'."), emitter, id, Vector3.Zero, !listener);
    }

    public void PostEventAt(string name, Vector3 position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!AudioPose.Finite(position)) throw new ArgumentException("A sound needs a finite position.", nameof(position));
        var sounds = _bank.Of(name) ?? throw new KeyNotFoundException($"Unknown sound event '{name}'.");
        foreach (var sound in sounds)
            if (sound.Loop) throw new InvalidOperationException($"'{name}' loops; a loop plays on an emitter, not at a point.");
        Play(name, sounds, null, 0, position, true);
    }

    private void Play(string name, SoundSpec[] sounds, Emitter? emitter, ulong follow, Vector3 position, bool positioned)
    {
        foreach (var sound in sounds)
        {
            if (sound.Loop)
            {
                // A loop already playing on the emitter is not started twice.
                emitter!.Loops.TryGetValue(name, out var loops);
                if (Playing(loops, sound.Index)) continue;
                var instance = _nextInstance++;
                (loops ??= emitter.Loops[name] = []).Add((sound.Index, instance));
                Stage(new MixCommand { Op = MixOp.Play, Id = instance, Emitter = follow, Position = position, Sound = sound, Rate = 1, Positioned = positioned, Clip = Pick(sound) });
            }
            else
            {
                var cents = sound.PitchCents * (2 * _random.NextSingle() - 1);
                Stage(new MixCommand
                {
                    Op = MixOp.Play, Emitter = follow, Position = position, Sound = sound, Rate = Math.Pow(2, cents / 1200), Positioned = positioned,
                    Clip = Pick(sound),
                });
            }
        }
    }

    private static bool Playing(List<(int Sound, ulong Instance)>? loops, int sound)
    {
        if (loops is null) return false;
        foreach (var loop in loops)
            if (loop.Sound == sound) return true;
        return false;
    }

    /// <summary>A variation at random, never the one played last when there are others.</summary>
    private int Pick(SoundSpec sound)
    {
        var count = sound.Clips.Length;
        var last = _lastClip[sound.Index];
        var clip = count == 1 ? 0 : last < 0 ? _random.Next(count) : (last + 1 + _random.Next(count - 1)) % count;
        _lastClip[sound.Index] = clip;
        return clip;
    }

    public void PlayMusic(string? score, bool fighting)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The arrangement goes with the fight whatever plays, a fixed score (--score) too. The score the
        // mixer plays now stays when it is the one asked for; else it is about to give way (at once, or once
        // the next has loaded) and leaves as the fight arranged it.
        if (fighting != _fighting)
        {
            _fighting = fighting;
            var staying = _fixedScore is not null || score == _heard;
            Stage(new MixCommand { Op = MixOp.Arrange, Fighting = fighting, Staying = staying });
            _leftArranged = !fighting && !staying;
        }
        if (_fixedScore is not null || score == _music) return;
        _music = score;
        // A load under way is for a score no longer wanted; asked back to what it has, the mixer goes on.
        _loading = null;
        if (score == _heard)
        {
            // Asked back before the next score came: the one playing stays after all, so it comes back whole.
            if (_leftArranged) Stage(new MixCommand { Op = MixOp.Arrange, Fighting = false, Staying = true });
            _leftArranged = false;
            return;
        }
        if (score is null)
        {
            Hear(null, null);
            return;
        }
        var load = _scores(score);
        if (load.IsCompleted) Hear(score, load.GetAwaiter().GetResult());
        // The music playing goes on until the next score has loaded.
        else _loading = (score, load);
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loading is { Load.IsCompleted: true } loading)
        {
            _loading = null;
            // A failed load is a missing or broken score, which --validate checks. Rethrown here on the
            // render thread, it ends the run with its error as any render-thread failure does, rather
            // than leaving the game silent.
            Hear(loading.Score, loading.Load.GetAwaiter().GetResult());
        }
        if (_staged.Count == 0) return;
        _mixer.Inbox.Post(_staged);
        _staged.Clear();
    }

    /// <summary>Hands the mixer <paramref name="score"/>'s <paramref name="music"/>, or silence for none.</summary>
    private void Hear(string? score, MusicPlayer? music)
    {
        _heard = score;
        // The score handed over starts as the mixer arranges it now.
        _leftArranged = false;
        Stage(new MixCommand { Op = MixOp.Music, Music = music });
    }

    private void Stage(in MixCommand command) => _staged.Add(command);

    private Emitter Require(ulong id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _emitters.TryGetValue(id, out var emitter) ? emitter : throw new InvalidOperationException($"Audio object {id} is not registered.");
    }

    /// <summary>Drops what was not committed; the host disposes the output that renders the mixer.</summary>
    public void Dispose()
    {
        _disposed = true;
        _staged.Clear();
        _emitters.Clear();
    }

    private sealed class Emitter
    {
        public bool Positioned;
        /// <summary>The loops each Play_ event started here: the sound and its instance.</summary>
        public readonly Dictionary<string, List<(int Sound, ulong Instance)>> Loops = new(StringComparer.Ordinal);
    }
}
