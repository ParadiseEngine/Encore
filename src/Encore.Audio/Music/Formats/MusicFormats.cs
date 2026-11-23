using System.Globalization;
using System.Text.Json;

namespace Encore.Audio.Music.Formats;

/// <summary>A file format songs can be read from. Implement it to teach the player another one.</summary>
public interface IMusicFormat
{
    /// <summary>A name for people and error messages.</summary>
    string Name { get; }

    /// <summary>The file name endings it usually has, with their dots (".mid").</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Whether <paramref name="data"/> looks like this format (its signature), without reading it all.</summary>
    bool CanRead(ReadOnlySpan<byte> data);

    /// <exception cref="InvalidDataException">The data is malformed, or asks for something the player cannot do.</exception>
    Song Read(ReadOnlySpan<byte> data);
}

/// <summary>The formats songs are read from, recognised by their content rather than their file names.</summary>
public static class MusicFormats
{
    /// <summary>This engine's <c>encore.song</c> JSON and Standard MIDI Files.</summary>
    public static IReadOnlyList<IMusicFormat> BuiltIn { get; } = [SongJsonFormat.Instance, MidiFormat.Instance];

    /// <summary>Reads <paramref name="data"/> with the first of <paramref name="formats"/> (by default <see cref="BuiltIn"/>) that recognises it.</summary>
    /// <param name="name">Names the data in errors (a file's path).</param>
    /// <exception cref="InvalidDataException">No format recognises the data, or the one that does finds it malformed.</exception>
    public static Song Read(ReadOnlySpan<byte> data, string? name = null, IEnumerable<IMusicFormat>? formats = null)
    {
        var candidates = formats ?? BuiltIn;
        foreach (var format in candidates)
        {
            if (!format.CanRead(data)) continue;
            try
            {
                return format.Read(data);
            }
            catch (InvalidDataException exception) when (name is not null)
            {
                throw new InvalidDataException($"{name}: {exception.Message}", exception);
            }
        }
        var known = string.Join(", ", candidates.Select(f => f.Name));
        throw new InvalidDataException($"{name ?? "The data"} is in none of the formats the player reads ({known}).");
    }

    /// <summary>Reads a song file. File exceptions propagate to the caller.</summary>
    /// <exception cref="ArgumentException">The path is null, empty or whitespace.</exception>
    /// <exception cref="InvalidDataException">No format recognises the file, or the one that does finds it malformed.</exception>
    public static Song Load(string path, IEnumerable<IMusicFormat>? formats = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(path), path, formats);
    }

    /// <summary>Reads a stream from its current position to its end, leaving it open.</summary>
    /// <exception cref="ArgumentException">The stream is null or unreadable.</exception>
    /// <exception cref="InvalidDataException">No format recognises the data, or the one that does finds it malformed.</exception>
    public static Song Load(Stream stream, IEnumerable<IMusicFormat>? formats = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Read(copy.GetBuffer().AsSpan(0, (int)copy.Length), null, formats);
    }
}

/// <summary>What the JSON formats share: finding a document's <c>format</c>, and reading objects strictly.</summary>
internal static class Json
{
    public static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> data) =>
        data.StartsWith("\uFEFF"u8) ? data[3..] : data;

    /// <summary>The top-level object's <c>format</c> string, or null when the data is no such JSON object.</summary>
    public static string? FormatName(ReadOnlySpan<byte> data)
    {
        try
        {
            var reader = new Utf8JsonReader(WithoutBom(data));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isFormat = reader.ValueTextEquals("format"u8);
                if (!reader.Read()) return null;
                if (isFormat) return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses a whole document; malformed JSON is <see cref="InvalidDataException"/>.</summary>
    public static JsonDocument Parse(ReadOnlySpan<byte> data, string what)
    {
        try
        {
            var reader = new Utf8JsonReader(WithoutBom(data));
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {what} is not valid JSON: {exception.Message}", exception);
        }
    }

    public static InvalidDataException Invalid(string message) => new(message);

    public static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// One JSON object read strictly: every property it has must be asked for (so a misspelt one is an error
/// rather than silently ignored), none may appear twice, and every value must have the asked-for type and range.
/// </summary>
internal sealed class JsonObject
{
    private readonly JsonElement element;
    private readonly HashSet<string> asked = new(StringComparer.Ordinal);

    public JsonObject(JsonElement element, string where)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Json.Invalid($"{where} must be a JSON object.");
        this.element = element;
        Where = where;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name))
                throw Json.Invalid($"{where} has '{property.Name}' twice.");
    }

    public string Where { get; }

    /// <summary>Every property of the object, for formats that read its names as data.</summary>
    public IEnumerable<JsonProperty> Properties => element.EnumerateObject();

    public bool TryGet(string name, out JsonElement value)
    {
        asked.Add(name);
        return element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    public double Number(string name, double fallback, double min = double.MinValue, double max = double.MaxValue) =>
        TryGet(name, out var value) ? Number(value, name, min, max) : fallback;

    public double? OptionalNumber(string name, double min = double.MinValue, double max = double.MaxValue) =>
        TryGet(name, out var value) ? Number(value, name, min, max) : null;

    public double RequiredNumber(string name, double min = double.MinValue, double max = double.MaxValue) =>
        TryGet(name, out var value) ? Number(value, name, min, max) : throw Json.Invalid($"{Where} needs '{name}'.");

    public long RequiredInteger(string name, long min, long max)
    {
        if (!TryGet(name, out var value)) throw Json.Invalid($"{Where} needs '{name}'.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var integer) || integer < min || integer > max)
            throw Json.Invalid($"{Where}: '{name}' must be a whole number in [{min}, {max}].");
        return integer;
    }

    public string? OptionalString(string name)
    {
        if (!TryGet(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Json.Invalid($"{Where}: '{name}' must be a string.");
    }

    public string RequiredString(string name) => OptionalString(name) ?? throw Json.Invalid($"{Where} needs '{name}'.");

    public bool Boolean(string name, bool fallback)
    {
        if (!TryGet(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Json.Invalid($"{Where}: '{name}' must be true or false."),
        };
    }

    public JsonObject? OptionalObject(string name) => TryGet(name, out var value) ? new JsonObject(value, $"{Where}.{name}") : null;

    public JsonElement RequiredArray(string name)
    {
        if (!TryGet(name, out var value)) throw Json.Invalid($"{Where} needs '{name}'.");
        return value.ValueKind == JsonValueKind.Array ? value : throw Json.Invalid($"{Where}: '{name}' must be an array.");
    }

    public JsonElement? OptionalArray(string name)
    {
        if (!TryGet(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Array ? value : throw Json.Invalid($"{Where}: '{name}' must be an array.");
    }

    /// <summary>Fails on any property nobody asked for.</summary>
    public void Done()
    {
        foreach (var property in element.EnumerateObject())
            if (!asked.Contains(property.Name))
                throw Json.Invalid($"{Where} has an unknown property '{property.Name}'.");
    }

    public double Number(JsonElement value, string name, double min, double max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Json.Invalid($"{Where}: '{name}' must be a finite number.");
        if (number < min || number > max)
            throw Json.Invalid($"{Where}: '{name}' must lie in [{Json.Format(min)}, {Json.Format(max)}].");
        return number;
    }
}
