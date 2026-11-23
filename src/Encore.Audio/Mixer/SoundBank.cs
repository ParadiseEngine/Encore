using System.Text.Json;

namespace Encore.Audio;

/// <summary>Where a sound plays: its own volume, under the master.</summary>
public enum AudioBus : byte { Effects, Ambience, Music }

/// <summary>A master's samples as they are on disk: 16-bit PCM at 48 kHz, one or two channels interleaved.</summary>
public sealed class SoundClip
{
    public SoundClip(short[] samples, int channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels is < 1 or > 2 || samples.Length == 0 || samples.Length % channels != 0)
            throw new ArgumentException("A clip holds whole frames of one or two channels.", nameof(samples));
        Samples = samples;
        Channels = channels;
    }

    public short[] Samples { get; }

    public int Channels { get; }

    public int Frames => Samples.Length / Channels;
}

/// <summary>One sound of the table (events.json): the variations it picks from and how the mixer plays them.</summary>
/// <param name="Index">Its place in <see cref="SoundBank.Sounds"/>.</param>
/// <param name="Gain">Linear volume (the table's volumeDb).</param>
/// <param name="PitchCents">The most a one-shot is detuned up or down, at random.</param>
/// <param name="MaxVoices">The most voices of it at once; a new one takes the oldest's place.</param>
/// <param name="MinIntervalFrames">Frames after one start within which another is dropped.</param>
/// <param name="Pool">The voices it shares with the other one-shots of its pool, or null.</param>
public sealed record SoundSpec(int Index, string Id, string Event, bool Loop, AudioBus Bus, bool Positioned, float Gain, float PitchCents,
    int MaxVoices, int MinIntervalFrames, float MinDistance, float MaxDistance, SoundClip[] Clips, VoicePool? Pool);

/// <summary>
/// A budget of voices one-shots share (the table's pools, such as the machines' footsteps): a start
/// within <paramref name="MinIntervalFrames"/> of the pool's last is dropped, and with
/// <paramref name="MaxVoices"/> of them sounding a new one takes the quietest one's place if it is
/// louder as heard, else it is dropped.
/// </summary>
/// <param name="Index">Its place in <see cref="SoundBank.Pools"/>.</param>
public sealed record VoicePool(int Index, string Id, int MaxVoices, int MinIntervalFrames);

/// <summary>
/// How the music plays while a fight is on (the table's <c>battleMusic</c>, design.md 戰鬥只留下節奏組、音效收短):
/// only the score's <paramref name="Parts"/> sound, at <paramref name="Gain"/>, every other part fading out over
/// <paramref name="LeaveSeconds"/> as the fight starts, and the whole score comes back over
/// <paramref name="ReturnSeconds"/> once it is over. No parts is silence while fighting. An untagged score's parts
/// cannot be told apart, so it plays whole at <paramref name="Gain"/>.
/// </summary>
public sealed record BattleMusic(IReadOnlyList<ScorePart> Parts, float Gain, float LeaveSeconds, float ReturnSeconds)
{
    /// <summary>Writes each part's gain (indexed by <see cref="ScorePart"/>) while <paramref name="fighting"/>, or 1 for every part out of a fight.</summary>
    public void Gains(bool fighting, Span<float> gains)
    {
        if (!fighting)
        {
            gains.Fill(1f);
            return;
        }
        gains.Clear();
        foreach (var part in Parts) gains[(int)part] = Gain;
        if (Parts.Count > 0) gains[(int)ScorePart.Untagged] = Gain;
    }
}

/// <summary>
/// The sound effects the game plays (assets/audio/sfx, written by
/// assets/audio/source/sfx/make_sfx.py): every sound of events.json with its masters decoded, found by
/// the event that plays it. A <c>Play_X</c> event starts its sounds; the table may give <c>Stop_X</c>
/// sounds of its own (the record winding down).
/// </summary>
public sealed class SoundBank
{
    public const string TableFile = "events.json";

    private readonly Dictionary<string, SoundSpec[]> _events;

    private SoundBank(VoicePool[] pools, SoundSpec[] sounds, BattleMusic? battle)
    {
        Pools = pools;
        Sounds = sounds;
        Battle = battle;
        _events = sounds.GroupBy(s => s.Event, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>The tree beside the executable that Encore.App ships.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "assets", "audio", "sfx");

    public IReadOnlyList<SoundSpec> Sounds { get; }

    public IReadOnlyList<VoicePool> Pools { get; }

    /// <summary>How the music plays while a fight is on, or null when the table leaves it whole.</summary>
    public BattleMusic? Battle { get; }

    /// <summary>The events the table plays, each once.</summary>
    public IEnumerable<string> Events => _events.Keys;

    /// <summary>The sounds event <paramref name="name"/> plays, or null when the table has none.</summary>
    public SoundSpec[]? Of(string name) => _events.GetValueOrDefault(name);

    /// <summary>
    /// Whether the engine can post <paramref name="name"/>: an event with sounds, or a control event
    /// (<c>Stop_X</c>, <c>Pause_X</c>, <c>Resume_X</c>) of one.
    /// </summary>
    public bool Knows(string name) => _events.ContainsKey(name) || AudioEvents.Controlled(name) is { } play && _events.ContainsKey(play);

    /// <summary>Loads events.json in <paramref name="directory"/> and decodes every master it lists.</summary>
    /// <exception cref="InvalidDataException">The table or a master is malformed.</exception>
    public static SoundBank Load(string directory)
    {
        var path = Path.Combine(directory, TableFile);
        return Load(File.ReadAllBytes(path), file => File.ReadAllBytes(Path.Combine(directory, file)), path);
    }

    /// <summary>
    /// Loads a table's bytes (an events.json) and decodes every master it lists, as
    /// <paramref name="read"/> hands them over by their path relative to the table (<see cref="Masters"/>);
    /// <paramref name="origin"/> is where the table came from, for errors. The browser host loads the
    /// files it downloaded this way.
    /// </summary>
    /// <exception cref="InvalidDataException">The table or a master is malformed.</exception>
    public static SoundBank Load(byte[] table, Func<string, byte[]> read, string origin)
    {
        ArgumentNullException.ThrowIfNull(read);
        var directory = Path.GetDirectoryName(origin) ?? "";
        using var document = JsonDocument.Parse(table);
        var pools = new Dictionary<string, VoicePool>(StringComparer.Ordinal);
        foreach (var pool in Array(document.RootElement, "pools", origin).EnumerateArray())
        {
            var id = Text(pool, "id", origin);
            var where = $"{origin}: pool '{id}'";
            var voices = Int(pool, "maxVoices", where);
            var interval = Number(pool, "minIntervalSeconds", where);
            if (voices < 1 || interval < 0) throw new InvalidDataException($"{where}: voices or interval out of range.");
            if (!pools.TryAdd(id, new VoicePool(pools.Count, id, voices, (int)MathF.Round(interval * WavFile.SampleRate))))
                throw new InvalidDataException($"{where} is listed twice.");
        }
        var sounds = new List<SoundSpec>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sound in SoundsOf(document.RootElement, origin))
        {
            var id = Text(sound, "id", origin);
            var where = $"{origin}: sound '{id}'";
            if (!ids.Add(id)) throw new InvalidDataException($"{where} is listed twice.");
            var loop = Text(sound, "kind", where) switch
            {
                "one-shot" => false,
                "loop" => true,
                var other => throw new InvalidDataException($"{where}: unknown kind '{other}'."),
            };
            var bus = Text(sound, "bus", where) switch
            {
                "effects" => AudioBus.Effects,
                "ambience" => AudioBus.Ambience,
                var other => throw new InvalidDataException($"{where}: unknown bus '{other}'."),
            };
            var positioned = Bool(sound, "positioned", where);
            var clips = FilesOf(sound, where).Select(file => WavFile.Read(Path.Combine(directory, file), read(file))).ToArray();
            if (clips.Length == 0) throw new InvalidDataException($"{where}: no files.");
            if (positioned && clips.Any(c => c.Channels != 1)) throw new InvalidDataException($"{where}: only a mono sound can be positioned.");
            var minDistance = Number(sound, "minDistance", where);
            var maxDistance = Number(sound, "maxDistance", where);
            var maxVoices = Int(sound, "maxVoices", where);
            var interval = Number(sound, "minIntervalSeconds", where);
            var pitch = Number(sound, "pitchCents", where);
            if (minDistance <= 0 || maxDistance <= minDistance || maxVoices < 1 || interval < 0 || pitch is < 0 or > 1200)
                throw new InvalidDataException($"{where}: distances, voices, interval or pitch out of range.");
            VoicePool? shared = null;
            if (Text(sound, "pool", where) is { Length: > 0 } poolId)
            {
                if (!pools.TryGetValue(poolId, out shared)) throw new InvalidDataException($"{where}: no pool '{poolId}'.");
                if (loop) throw new InvalidDataException($"{where}: only a one-shot shares a pool's voices.");
            }
            sounds.Add(new SoundSpec(sounds.Count, id, Text(sound, "event", where), loop, bus, positioned,
                MathF.Pow(10, Number(sound, "volumeDb", where) / 20), pitch, maxVoices, (int)MathF.Round(interval * WavFile.SampleRate),
                minDistance, maxDistance, clips, shared));
        }
        var bank = new SoundBank([.. pools.Values.OrderBy(p => p.Index)], [.. sounds], BattleOf(document.RootElement, origin));
        foreach (var name in bank.Events)
            if (!name.StartsWith("Play_", StringComparison.Ordinal) && (AudioEvents.Controlled(name) is not { } play || !bank._events.ContainsKey(play)))
                throw new InvalidDataException($"{origin}: event '{name}' is neither a Play_ event nor the Stop_, Pause_ or Resume_ of one.");
        return bank;
    }

    /// <summary>The table's optional <c>battleMusic</c>, checked: known parts, each once, a volume of at most 0 dB and positive times.</summary>
    private static BattleMusic? BattleOf(JsonElement root, string origin)
    {
        if (!root.TryGetProperty("battleMusic", out var battle)) return null;
        var where = $"{origin}: battleMusic";
        var parts = new List<ScorePart>();
        foreach (var name in Array(battle, "parts", where).EnumerateArray())
        {
            var part = ScoreParts.Named(name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                ?? throw new InvalidDataException($"{where}: '{name}' is not a part (kick, snare, hats, bass, keys, pad or melody).");
            if (parts.Contains(part)) throw new InvalidDataException($"{where}: '{name}' is listed twice.");
            parts.Add(part);
        }
        var volume = Number(battle, "volumeDb", where);
        var spec = new BattleMusic(parts, MathF.Pow(10, volume / 20), Number(battle, "leaveSeconds", where), Number(battle, "returnSeconds", where));
        if (volume is < -60 or > 0 || spec.LeaveSeconds is <= 0 or > 30 || spec.ReturnSeconds is <= 0 or > 30)
            throw new InvalidDataException($"{where}: volume or times out of range.");
        return spec;
    }

    /// <summary>The masters a table's bytes list, by their path relative to it, each once, in the table's order: what <see cref="Load(byte[], Func{string, byte[]}, string)"/> reads.</summary>
    /// <exception cref="InvalidDataException">The table is malformed.</exception>
    public static IReadOnlyList<string> Masters(byte[] table, string origin)
    {
        using var document = JsonDocument.Parse(table);
        var masters = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sound in SoundsOf(document.RootElement, origin))
            foreach (var file in FilesOf(sound, $"{origin}: sound '{Text(sound, "id", origin)}'"))
                if (seen.Add(file)) masters.Add(file);
        return masters;
    }

    private static JsonElement.ArrayEnumerator SoundsOf(JsonElement root, string origin)
    {
        if (Int(root, "schemaVersion", origin) != 1 || Text(root, "kind", origin) != "encore.sound-table" || Int(root, "sampleRate", origin) != WavFile.SampleRate)
            throw new InvalidDataException($"{origin}: not a version 1 encore.sound-table at 48 kHz.");
        return Array(root, "sounds", origin).EnumerateArray();
    }

    private static IEnumerable<string> FilesOf(JsonElement sound, string where) =>
        Array(sound, "files", where).EnumerateArray().Select(f => f.GetString() ?? throw new InvalidDataException($"{where}: a file is not a string."));

    private static JsonElement Field(JsonElement element, string name, string where) =>
        element.TryGetProperty(name, out var value) ? value : throw new InvalidDataException($"{where}: no '{name}'.");

    private static string Text(JsonElement element, string name, string where) =>
        Field(element, name, where) is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new InvalidDataException($"{where}: '{name}' is not a string.");

    private static float Number(JsonElement element, string name, string where) =>
        Field(element, name, where) is { ValueKind: JsonValueKind.Number } value && float.IsFinite(value.GetSingle())
            ? value.GetSingle() : throw new InvalidDataException($"{where}: '{name}' is not a finite number.");

    private static int Int(JsonElement element, string name, string where) =>
        Field(element, name, where) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number : throw new InvalidDataException($"{where}: '{name}' is not an integer.");

    private static bool Bool(JsonElement element, string name, string where) =>
        Field(element, name, where) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : throw new InvalidDataException($"{where}: '{name}' is not true or false.");

    private static JsonElement Array(JsonElement element, string name, string where) =>
        Field(element, name, where) is { ValueKind: JsonValueKind.Array } value ? value : throw new InvalidDataException($"{where}: '{name}' is not a list.");
}
