using System.Numerics;

namespace Encore.Audio;

/// <summary>
/// Sound commands on the owning host thread: emitters are registered ids that follow a pose, and
/// events (<see cref="AudioEvents"/>) play on an emitter, on the listener itself (unpositioned) or at a
/// fixed point. Poses use the game's right-handed, Y-up metres. Commands take effect together at
/// <see cref="Commit"/>.
/// </summary>
public interface IAudioEngine : IDisposable
{
    void RegisterObject(ulong id);

    /// <summary>Forgets emitter <paramref name="id"/>: its loops fade out, its one-shots play out where it last stood.</summary>
    void UnregisterObject(ulong id);

    void SetListener(ulong id);
    void SetPosition(ulong id, AudioPose pose);

    /// <summary>
    /// Plays or controls <paramref name="name"/> on emitter <paramref name="id"/>, which must have a
    /// position; on the listener it plays unpositioned. A loop already playing on the emitter is not
    /// started twice.
    /// </summary>
    void PostEvent(string name, ulong id);

    /// <summary>Plays <paramref name="name"/> once at a fixed world point (a hit, a burst), with no emitter to keep.</summary>
    void PostEventAt(string name, Vector3 position);

    /// <summary>
    /// Crossfades the music to <paramref name="score"/> (a song under assets/audio/scores, named
    /// without its file ending: .song.json, .mid or .midi) once it has loaded, or fades it out for null; asking for the music playing
    /// or loading never starts it over, and a score still loading that is no longer asked for is dropped.
    /// While <paramref name="fighting"/> the music plays as the sound table's battleMusic arranges a fight
    /// (its rhythm parts only, quieter); asked again without it, the whole score comes back.
    /// </summary>
    void PlayMusic(string? score, bool fighting);

    /// <summary>Hands this frame's commands to the mixer at once.</summary>
    void Commit();
}

public readonly record struct AudioPose(Vector3 Position, Vector3 Forward, Vector3 Up)
{
    public static AudioPose LookingAt(Vector3 position, Vector3 target)
    {
        var forward = target - position;
        if (!Finite(position) || !Finite(forward) || !float.IsFinite(forward.LengthSquared()) || forward.LengthSquared() < 1e-8f)
            throw new ArgumentException("An audio pose needs a finite position and a nonzero forward direction.");
        forward = Vector3.Normalize(forward);
        var referenceUp = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) > .999f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(forward, referenceUp));
        return new AudioPose(position, forward, Vector3.Cross(right, forward));
    }

    /// <summary>The listener's right hand: the direction a sound pans fully right from.</summary>
    public Vector3 Right => Vector3.Cross(Forward, Up);

    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
