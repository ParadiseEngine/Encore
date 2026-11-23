namespace Encore.Audio;

/// <summary>
/// How the sound table (events.json) names its events. A <c>Play_X</c> event starts its sounds;
/// <c>Stop_X</c>, <c>Pause_X</c> and <c>Resume_X</c> act on the voices <c>Play_X</c> started on the same
/// emitter, then play any sounds of their own. The event names themselves belong to the host: a game
/// keeps its own constants beside the code that posts them.
/// </summary>
public static class AudioEvents
{
    private const string Play = "Play_";

    /// <summary>The event that stops the voices <paramref name="play"/> started on the same emitter.</summary>
    public static string Stop(string play) => Control("Stop_", play);

    /// <summary>The event that pauses the voices <paramref name="play"/> started on the same emitter.</summary>
    public static string Pause(string play) => Control("Pause_", play);

    /// <summary>The event that resumes the voices <see cref="Pause"/> paused.</summary>
    public static string Resume(string play) => Control("Resume_", play);

    /// <summary>The <c>Play_X</c> a control event (<c>Stop_X</c>, <c>Pause_X</c>, <c>Resume_X</c>) acts on, or null for a play event.</summary>
    public static string? Controlled(string name)
    {
        foreach (var prefix in (ReadOnlySpan<string>)["Stop_", "Pause_", "Resume_"])
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return string.Concat(Play, name.AsSpan(prefix.Length));
        return null;
    }

    private static string Control(string prefix, string play) =>
        play.StartsWith(Play, StringComparison.Ordinal)
            ? string.Concat(prefix, play.AsSpan(Play.Length))
            : throw new ArgumentException($"'{play}' is not a Play_ event.", nameof(play));
}
