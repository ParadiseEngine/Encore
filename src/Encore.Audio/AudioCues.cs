using System.Numerics;

namespace Encore.Audio;

/// <summary>
/// An engine that writes down what the presenters post through it, for checking a mix without ears:
/// each event and each music change with the time it was posted (<see cref="Time"/>, set by the
/// caller), and how often each event was posted. Everything else passes straight through.
/// </summary>
public sealed class AudioCues(IAudioEngine engine) : IAudioEngine
{
    private readonly List<(double Time, string Cue)> _cues = [];
    private string? _music;
    private bool _fighting;

    /// <summary>The time, in seconds, the next posts are written down at.</summary>
    public double Time { get; set; }

    /// <summary>
    /// Every event posted and every change of music (<c>music &lt;score&gt;</c>, <c>music -</c> for silence;
    /// <c>music fight</c> and <c>music whole</c> as a fight's arrangement starts and ends), in order.
    /// </summary>
    public IReadOnlyList<(double Time, string Cue)> Cues => _cues;

    /// <summary>How many times each event was posted.</summary>
    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public void RegisterObject(ulong id) => engine.RegisterObject(id);
    public void UnregisterObject(ulong id) => engine.UnregisterObject(id);
    public void SetListener(ulong id) => engine.SetListener(id);
    public void SetPosition(ulong id, AudioPose pose) => engine.SetPosition(id, pose);
    public void Commit() => engine.Commit();
    public void Dispose() => engine.Dispose();

    public void PostEvent(string name, ulong id)
    {
        engine.PostEvent(name, id);
        Note(name);
    }

    public void PostEventAt(string name, Vector3 position)
    {
        engine.PostEventAt(name, position);
        Note(name);
    }

    public void PlayMusic(string? score, bool fighting)
    {
        engine.PlayMusic(score, fighting);
        if (fighting != _fighting)
        {
            _fighting = fighting;
            _cues.Add((Time, fighting ? "music fight" : "music whole"));
        }
        if (score == _music) return;
        _music = score;
        _cues.Add((Time, $"music {score ?? "-"}"));
    }

    /// <summary>Writes the cues as tab-separated lines: seconds, then the cue.</summary>
    public void Write(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var (time, cue) in _cues) output.WriteLine($"{time:F3}\t{cue}");
    }

    private void Note(string name)
    {
        _cues.Add((Time, name));
        Counts[name] = Counts.GetValueOrDefault(name) + 1;
    }
}
