using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Encore.Audio.Music.Formats;

/// <summary>
/// This engine's own format, <c>encore.song</c> version 1: a JSON document that says everything a
/// <see cref="Song"/> holds, with times in beats at one tempo.
/// </summary>
/// <remarks>
/// <code>
/// {
///   "format": "encore.song", "version": 1,
///   "title": "Optional",
///   "bpm": 120,                  // beats a minute
///   "length": 16,                // beats until the song repeats
///   "seed": 1,                   // optional: seeds noises and unison phases
///   "effects": [ { "reverbSeconds": 1.6, "reverbSize": 0.5, "reverbDamping": 9000,
///                  "delayBeats": 0.75, "delayFeedback": 0.35, "delayDamping": 6000 } ],
///   "instruments": [ {
///     "name": "bass", "part": "bass", "wave": "sawtooth", "gain": 0.3,
///     "envelope": { "attack": 0.003, "decay": 0.2, "sustain": 0.5, "release": 0.05 },
///     "filters": [ { "mode": "lowpass", "cutoff": 500, "resonance": 2 } ],
///     "modulations": [ { "target": "cutoff", "filter": 0, "amount": 2, "envelope": { "decay": 0.15, "sustain": 0 } } ],
///     "sends": { "bus": 0, "reverb": 0.2 } } ],
///   "notes": [ [0, 0.5, 0, 36, 0.9] ]   // [beat, beats, instrument, pitch (MIDI, or null), velocity]
/// }
/// </code>
/// Every instrument property is optional, named as on <see cref="Instrument"/> in camelCase: <c>name</c>,
/// <c>part</c>, <c>wave</c> (sine, triangle, sawtooth, square, pulse, white, pink, brown), <c>pulseWidth</c>,
/// <c>unison</c> (<c>voices</c>, <c>detune</c>, <c>spread</c>), <c>fm</c> (<c>ratio</c>, <c>index</c>),
/// <c>noise</c>, <c>envelope</c>, <c>filters</c> (each <c>mode</c> lowpass, highpass or bandpass,
/// <c>cutoff</c>, <c>resonance</c>, <c>steep</c>), <c>drive</c>, <c>phaser</c> (<c>rate</c>, <c>depth</c>,
/// <c>center</c>, <c>sweep</c>), <c>modulations</c> (each <c>target</c> pitch, cutoff, amplitude, pulseWidth
/// or fmIndex, <c>amount</c>, <c>filter</c> for a cutoff, and either <c>lfo</c> (<c>rate</c>, <c>shape</c>
/// sine, triangle, square or saw, <c>phase</c>) or <c>envelope</c>), <c>gain</c>, <c>pan</c>, <c>sends</c>
/// (<c>bus</c>, <c>dry</c>, <c>reverb</c>, <c>delay</c>) and <c>defaultPitch</c>. Envelope times, rates and
/// frequencies are in seconds and hertz, not beats; an envelope's missing fields are the default envelope's
/// (attack 0.005, decay 0, sustain 1, release 0.05). A note's pitch and velocity may be left off (the
/// instrument's default pitch, velocity 1). Unknown and repeated properties are errors.
/// </remarks>
public sealed class SongJsonFormat : IMusicFormat
{
    public const string FormatName = "encore.song";
    public const int Version = 1;

    private static readonly Instrument Defaults = new();
    private static readonly EffectBus BusDefaults = new();
    private static readonly Sends SendDefaults = new(0);

    private SongJsonFormat()
    {
    }

    public static SongJsonFormat Instance { get; } = new();

    public string Name => "Encore song (encore.song JSON)";

    public IReadOnlyList<string> Extensions { get; } = [".song.json"];

    public bool CanRead(ReadOnlySpan<byte> data) => Json.FormatName(data) == FormatName;

    public Song Read(ReadOnlySpan<byte> data)
    {
        using var document = Json.Parse(data, "song");
        var root = new JsonObject(document.RootElement, "The song");
        if (root.RequiredString("format") != FormatName)
            throw Json.Invalid($"The song's format is not '{FormatName}'.");
        var version = root.RequiredInteger("version", 0, int.MaxValue);
        if (version != Version)
            throw Json.Invalid($"Unsupported {FormatName} version {version}; this player reads version {Version}.");
        var title = root.OptionalString("title");
        var beat = 60 / root.RequiredNumber("bpm", 1, 10_000);
        var length = root.RequiredNumber("length", 0, double.MaxValue);
        var seed = (uint)root.Number("seed", 1, 0, uint.MaxValue);

        var effects = new List<EffectBus>();
        if (root.OptionalArray("effects") is { } effectArray)
            foreach (var element in effectArray.EnumerateArray())
                effects.Add(ReadBus(new JsonObject(element, $"Effect bus {effects.Count}"), beat));

        var instruments = new List<Instrument>();
        foreach (var element in root.RequiredArray("instruments").EnumerateArray())
            instruments.Add(ReadInstrument(new JsonObject(element, $"Instrument {instruments.Count}")));

        var notes = new List<Note>();
        foreach (var element in root.RequiredArray("notes").EnumerateArray())
            notes.Add(ReadNote(element, notes.Count, beat));
        root.Done();

        try
        {
            return new Song(instruments, notes, length * beat, effects, seed, title);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    /// <summary>Writes <paramref name="song"/> as an <c>encore.song</c> document (at 60 beats a minute, so beats are seconds), leaving out what is at its default.</summary>
    public static string Write(Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        var text = new StringBuilder();
        text.Append("{\n");
        text.Append($"  \"format\": \"{FormatName}\",\n  \"version\": {Version},\n");
        if (song.Title is { } title) text.Append($"  \"title\": {Quote(title)},\n");
        text.Append($"  \"bpm\": 60,\n  \"length\": {F(song.Length)},\n  \"seed\": {song.Seed},\n");
        AppendList(text, "effects", song.Effects.Select(WriteBus).ToList(), last: false);
        AppendList(text, "instruments", song.Instruments.Select(WriteInstrument).ToList(), last: false);
        AppendList(text, "notes", song.Notes.Select(n =>
            $"[{F(n.Start)}, {F(n.Duration)}, {n.Instrument}, {(n.Pitch is { } pitch ? F(pitch) : "null")}, {n.Velocity.ToString("R", CultureInfo.InvariantCulture)}]").ToList(), last: true);
        text.Append("}\n");
        return text.ToString();
    }

    private static void AppendList(StringBuilder text, string name, List<string> items, bool last)
    {
        text.Append($"  \"{name}\": [");
        for (var i = 0; i < items.Count; i++)
            text.Append(i == 0 ? "\n    " : ",\n    ").Append(items[i]);
        text.Append(items.Count > 0 ? "\n  ]" : "]").Append(last ? "\n" : ",\n");
    }

    private static EffectBus ReadBus(JsonObject json, double beat)
    {
        var delayBeats = json.OptionalNumber("delayBeats", 0, double.MaxValue);
        var delaySeconds = json.OptionalNumber("delaySeconds", 0, double.MaxValue);
        if (delayBeats is not null && delaySeconds is not null)
            throw Json.Invalid($"{json.Where} gives both 'delayBeats' and 'delaySeconds'.");
        var bus = new EffectBus
        {
            ReverbSeconds = json.Number("reverbSeconds", BusDefaults.ReverbSeconds),
            ReverbSize = json.Number("reverbSize", BusDefaults.ReverbSize),
            ReverbDamping = json.Number("reverbDamping", BusDefaults.ReverbDamping),
            DelaySeconds = delaySeconds ?? delayBeats * beat ?? BusDefaults.DelaySeconds,
            DelayFeedback = json.Number("delayFeedback", BusDefaults.DelayFeedback),
            DelayDamping = json.Number("delayDamping", BusDefaults.DelayDamping),
        };
        json.Done();
        return bus;
    }

    private static Instrument ReadInstrument(JsonObject json)
    {
        var d = Defaults;
        var partName = json.OptionalString("part");
        var part = partName is null ? ScorePart.Untagged : ScoreParts.Named(partName)
            ?? throw Json.Invalid($"{json.Where}: unknown part '{partName}' (kick, snare, hats, bass, keys, pad or melody).");
        var waveName = json.OptionalString("wave");
        var wave = waveName is null ? d.Waveform : Names.ParseWaveform(waveName)
            ?? throw Json.Invalid($"{json.Where}: unknown wave '{waveName}' (sine, triangle, sawtooth, square, pulse, white, pink or brown).");

        Unison? unison = null;
        if (json.OptionalObject("unison") is { } u)
        {
            unison = new Unison((int)u.RequiredNumber("voices", 1, Song.MaxUnison), u.Number("detune", 0), u.Number("spread", 0));
            u.Done();
        }
        Fm? fm = null;
        if (json.OptionalObject("fm") is { } f)
        {
            fm = new Fm(f.Number("ratio", 1), f.Number("index", 0));
            f.Done();
        }
        Phaser? phaser = null;
        if (json.OptionalObject("phaser") is { } ph)
        {
            phaser = new Phaser(ph.RequiredNumber("rate"), ph.RequiredNumber("depth"), ph.RequiredNumber("center"), ph.RequiredNumber("sweep"));
            ph.Done();
        }
        Sends? sends = null;
        if (json.OptionalObject("sends") is { } s)
        {
            sends = new Sends((int)s.Number("bus", 0, 0, int.MaxValue))
            {
                Dry = s.Number("dry", SendDefaults.Dry),
                Reverb = s.Number("reverb", SendDefaults.Reverb),
                Delay = s.Number("delay", SendDefaults.Delay),
            };
            s.Done();
        }

        var filters = new List<Filter>();
        if (json.OptionalArray("filters") is { } filterArray)
            foreach (var element in filterArray.EnumerateArray())
                filters.Add(ReadFilter(new JsonObject(element, $"{json.Where} filter {filters.Count}")));
        var modulations = new List<Modulation>();
        if (json.OptionalArray("modulations") is { } modulationArray)
            foreach (var element in modulationArray.EnumerateArray())
                modulations.Add(ReadModulation(new JsonObject(element, $"{json.Where} modulation {modulations.Count}")));

        var instrument = new Instrument
        {
            Name = json.OptionalString("name") ?? d.Name,
            Part = part,
            Waveform = wave,
            PulseWidth = json.Number("pulseWidth", d.PulseWidth),
            Unison = unison,
            Fm = fm,
            Noise = json.Number("noise", d.Noise),
            Envelope = ReadEnvelope(json, "envelope") ?? d.Envelope,
            Filters = filters,
            Drive = json.Number("drive", d.Drive),
            Phaser = phaser,
            Modulations = modulations,
            Gain = json.Number("gain", d.Gain),
            Pan = json.Number("pan", d.Pan),
            Sends = sends,
            DefaultPitch = json.Number("defaultPitch", d.DefaultPitch),
        };
        json.Done();
        return instrument;
    }

    private static Filter ReadFilter(JsonObject json)
    {
        var modeName = json.RequiredString("mode");
        var mode = Names.ParseMode(modeName) ?? throw Json.Invalid($"{json.Where}: unknown mode '{modeName}' (lowpass, highpass or bandpass).");
        var filter = new Filter(mode, json.RequiredNumber("cutoff"))
        {
            Resonance = json.Number("resonance", new Filter(mode, 1).Resonance),
            Steep = json.Boolean("steep", false),
        };
        json.Done();
        return filter;
    }

    private static Modulation ReadModulation(JsonObject json)
    {
        var targetName = json.RequiredString("target");
        var target = Names.ParseTarget(targetName)
            ?? throw Json.Invalid($"{json.Where}: unknown target '{targetName}' (pitch, cutoff, amplitude, pulseWidth or fmIndex).");
        Lfo? lfo = null;
        if (json.OptionalObject("lfo") is { } l)
        {
            var shapeName = l.OptionalString("shape");
            lfo = new Lfo(l.RequiredNumber("rate"))
            {
                Shape = shapeName is null ? LfoShape.Sine : Names.ParseShape(shapeName)
                    ?? throw Json.Invalid($"{l.Where}: unknown shape '{shapeName}' (sine, triangle, square or saw)."),
                Phase = l.Number("phase", 0),
            };
            l.Done();
        }
        var modulation = new Modulation(target, json.RequiredNumber("amount"))
        {
            Lfo = lfo,
            Envelope = ReadEnvelope(json, "envelope"),
            Filter = (int)json.Number("filter", 0, 0, Song.MaxFilters),
        };
        json.Done();
        return modulation;
    }

    private static Envelope? ReadEnvelope(JsonObject parent, string name)
    {
        if (parent.OptionalObject(name) is not { } json) return null;
        var d = Envelope.Held;
        var envelope = new Envelope(json.Number("attack", d.Attack), json.Number("decay", d.Decay), json.Number("sustain", d.Sustain), json.Number("release", d.Release));
        json.Done();
        return envelope;
    }

    private static Note ReadNote(JsonElement element, int index, double beat)
    {
        var where = $"Note {index}";
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() is < 3 or > 5)
            throw Json.Invalid($"{where} must be an array [beat, beats, instrument, pitch?, velocity?].");
        double Number(int at, string name)
        {
            var value = element[at];
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                throw Json.Invalid($"{where}: its {name} must be a finite number.");
            return number;
        }
        var count = element.GetArrayLength();
        if (element[2].ValueKind != JsonValueKind.Number || !element[2].TryGetInt32(out var instrument))
            throw Json.Invalid($"{where}: its instrument must be a whole number.");
        double? pitch = count > 3 && element[3].ValueKind != JsonValueKind.Null ? Number(3, "pitch") : null;
        var velocity = count > 4 ? Number(4, "velocity") : 1;
        return new Note(Number(0, "beat") * beat, Number(1, "length") * beat, pitch, (float)velocity, instrument);
    }

    private static string WriteBus(EffectBus bus)
    {
        var o = new Fields();
        o.Number("reverbSeconds", bus.ReverbSeconds, BusDefaults.ReverbSeconds);
        o.Number("reverbSize", bus.ReverbSize, BusDefaults.ReverbSize);
        o.Number("reverbDamping", bus.ReverbDamping, BusDefaults.ReverbDamping);
        o.Number("delaySeconds", bus.DelaySeconds, BusDefaults.DelaySeconds);
        o.Number("delayFeedback", bus.DelayFeedback, BusDefaults.DelayFeedback);
        o.Number("delayDamping", bus.DelayDamping, BusDefaults.DelayDamping);
        return o.Inline();
    }

    private static string WriteInstrument(Instrument instrument)
    {
        var d = Defaults;
        var o = new Fields();
        if (instrument.Name.Length > 0) o.Raw("name", Quote(instrument.Name));
        if (ScoreParts.Name(instrument.Part) is { } part) o.Raw("part", Quote(part));
        if (instrument.Waveform != d.Waveform) o.Raw("wave", Quote(Names.Of(instrument.Waveform)));
        o.Number("pulseWidth", instrument.PulseWidth, d.PulseWidth);
        if (instrument.Unison is { } unison)
            o.Raw("unison", $"{{\"voices\": {unison.Voices}, \"detune\": {F(unison.Detune)}, \"spread\": {F(unison.Spread)}}}");
        if (instrument.Fm is { } fm) o.Raw("fm", $"{{\"ratio\": {F(fm.Ratio)}, \"index\": {F(fm.Index)}}}");
        o.Number("noise", instrument.Noise, d.Noise);
        if (instrument.Envelope != d.Envelope) o.Raw("envelope", WriteEnvelope(instrument.Envelope));
        if (instrument.Filters.Count > 0) o.Raw("filters", "[" + string.Join(", ", instrument.Filters.Select(WriteFilter)) + "]");
        o.Number("drive", instrument.Drive, d.Drive);
        if (instrument.Phaser is { } phaser)
            o.Raw("phaser", $"{{\"rate\": {F(phaser.Rate)}, \"depth\": {F(phaser.Depth)}, \"center\": {F(phaser.Center)}, \"sweep\": {F(phaser.Sweep)}}}");
        if (instrument.Modulations.Count > 0)
            o.Raw("modulations", "[\n      " + string.Join(",\n      ", instrument.Modulations.Select(WriteModulation)) + "\n    ]");
        o.Number("gain", instrument.Gain, d.Gain);
        o.Number("pan", instrument.Pan, d.Pan);
        if (instrument.Sends is { } sends)
        {
            var s = new Fields();
            s.Raw("bus", sends.Bus.ToString(CultureInfo.InvariantCulture));
            s.Number("dry", sends.Dry, SendDefaults.Dry);
            s.Number("reverb", sends.Reverb, SendDefaults.Reverb);
            s.Number("delay", sends.Delay, SendDefaults.Delay);
            o.Raw("sends", s.Inline());
        }
        o.Number("defaultPitch", instrument.DefaultPitch, d.DefaultPitch);
        return o.Block("    ");
    }

    private static string WriteFilter(Filter filter)
    {
        var o = new Fields();
        o.Raw("mode", Quote(Names.Of(filter.Mode)));
        o.Raw("cutoff", F(filter.Cutoff));
        o.Number("resonance", filter.Resonance, new Filter(filter.Mode, 1).Resonance);
        if (filter.Steep) o.Raw("steep", "true");
        return o.Inline();
    }

    private static string WriteModulation(Modulation modulation)
    {
        var o = new Fields();
        o.Raw("target", Quote(Names.Of(modulation.Target)));
        o.Raw("amount", F(modulation.Amount));
        if (modulation.Target == ModulationTarget.Cutoff) o.Raw("filter", modulation.Filter.ToString(CultureInfo.InvariantCulture));
        if (modulation.Lfo is { } lfo)
        {
            var l = new Fields();
            l.Raw("rate", F(lfo.Rate));
            if (lfo.Shape != LfoShape.Sine) l.Raw("shape", Quote(Names.Of(lfo.Shape)));
            l.Number("phase", lfo.Phase, 0);
            o.Raw("lfo", l.Inline());
        }
        if (modulation.Envelope is { } envelope) o.Raw("envelope", WriteEnvelope(envelope));
        return o.Inline();
    }

    private static string WriteEnvelope(Envelope envelope) =>
        $"{{\"attack\": {F(envelope.Attack)}, \"decay\": {F(envelope.Decay)}, \"sustain\": {F(envelope.Sustain)}, \"release\": {F(envelope.Release)}}}";

    private static string F(double value) => Json.Format(value);

    private static string Quote(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    /// <summary>The format's names for the model's enums, both ways.</summary>
    private static class Names
    {
        private static readonly string[] Waveforms = ["sine", "triangle", "sawtooth", "square", "pulse", "white", "pink", "brown"];
        private static readonly string[] Modes = ["lowpass", "highpass", "bandpass"];
        private static readonly string[] Targets = ["pitch", "cutoff", "amplitude", "pulseWidth", "fmIndex"];
        private static readonly string[] Shapes = ["sine", "triangle", "square", "saw"];

        public static Waveform? ParseWaveform(string name) => Find(Waveforms, name) is { } i ? (Waveform)i : null;
        public static FilterMode? ParseMode(string name) => Find(Modes, name) is { } i ? (FilterMode)i : null;
        public static ModulationTarget? ParseTarget(string name) => Find(Targets, name) is { } i ? (ModulationTarget)i : null;
        public static LfoShape? ParseShape(string name) => Find(Shapes, name) is { } i ? (LfoShape)i : null;

        public static string Of(Waveform value) => Waveforms[(int)value];
        public static string Of(FilterMode value) => Modes[(int)value];
        public static string Of(ModulationTarget value) => Targets[(int)value];
        public static string Of(LfoShape value) => Shapes[(int)value];

        private static int? Find(string[] names, string name)
        {
            var index = Array.IndexOf(names, name);
            return index >= 0 ? index : null;
        }
    }

    /// <summary>An object's "name": value pairs, written on one line or one a line.</summary>
    private sealed class Fields
    {
        private readonly List<string> pairs = [];

        public void Raw(string name, string json) => pairs.Add($"\"{name}\": {json}");

        public void Number(string name, double value, double fallback)
        {
            if (value != fallback) Raw(name, F(value));
        }

        public string Inline() => "{" + string.Join(", ", pairs) + "}";

        public string Block(string indent) => pairs.Count == 0
            ? "{}"
            : "{\n" + indent + "  " + string.Join(",\n" + indent + "  ", pairs) + "\n" + indent + "}";
    }
}
