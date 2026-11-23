namespace Encore.Audio;

/// <summary>
/// The role an instrument plays in a song, by which the mixer arranges the music (the sound table's
/// <c>battleMusic</c>): a fight can keep the rhythm parts and fade the rest. An instrument with no role is
/// <see cref="Untagged"/>.
/// </summary>
public enum ScorePart : byte { Untagged, Kick, Snare, Hats, Bass, Keys, Pad, Melody }

public static class ScoreParts
{
    /// <summary>Every part, <see cref="ScorePart.Untagged"/> included: the length of an arrangement's gains.</summary>
    public const int Count = 8;

    /// <summary>The part named <paramref name="name"/> as songs and the sound table write it, or null for any other name.</summary>
    public static ScorePart? Named(string? name) => name switch
    {
        "kick" => ScorePart.Kick,
        "snare" => ScorePart.Snare,
        "hats" => ScorePart.Hats,
        "bass" => ScorePart.Bass,
        "keys" => ScorePart.Keys,
        "pad" => ScorePart.Pad,
        "melody" => ScorePart.Melody,
        _ => null,
    };

    /// <summary>The name <see cref="Named"/> reads back, or null for <see cref="ScorePart.Untagged"/>.</summary>
    public static string? Name(ScorePart part) => part switch
    {
        ScorePart.Kick => "kick",
        ScorePart.Snare => "snare",
        ScorePart.Hats => "hats",
        ScorePart.Bass => "bass",
        ScorePart.Keys => "keys",
        ScorePart.Pad => "pad",
        ScorePart.Melody => "melody",
        _ => null,
    };
}
