using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Domain.Midi;
using Cadence.Domain.Projects;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using static Cadence.Infrastructure.Projects.NodeReader;

namespace Cadence.Infrastructure.Projects;

/// <summary>
/// Reads and writes the Cadence project format: versioned, indented JSON that diffs well in version
/// control. Older formats are upgraded through <see cref="IProjectMigration"/>s before reading; newer
/// formats are refused with <see cref="ProjectVersionException"/>. See docs/project-format.md.
/// </summary>
public sealed class ProjectSerializer
{
    public const string FormatName = "cadence-project";
    public const int CurrentFormatVersion = 1;
    public const int MaxBytes = 256 * 1024 * 1024;

    private const int MaxTracks = 4096;
    private const int MaxEventsPerTrack = 5_000_000;
    private const int MaxNameLength = 1024;
    private static readonly string[] KnownRootProperties = ["format", "formatVersion", "project"];

    private readonly int _currentVersion;
    private readonly Dictionary<int, IProjectMigration> _migrations;

    public ProjectSerializer()
        : this(CurrentFormatVersion, [])
    {
    }

    internal ProjectSerializer(int currentVersion, IEnumerable<IProjectMigration> migrations)
    {
        _currentVersion = currentVersion;
        _migrations = migrations.ToDictionary(m => m.FromVersion);
    }

    public static ProjectSerializer Default { get; } = new();

    public byte[] Serialize(ProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var project = document.Project;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", FormatName);
            writer.WriteNumber("formatVersion", _currentVersion);
            writer.WriteStartObject("project");
            writer.WriteString("id", project.Id.Value.ToString("D"));
            writer.WriteString("name", project.Name);
            if (project.Loop is { } loop)
            {
                writer.WriteStartObject("loop");
                writer.WriteNumber("start", loop.Start.Value);
                writer.WriteNumber("end", loop.End.Value);
                writer.WriteEndObject();
            }

            WriteSequence(writer, project.Sequence);
            WriteRouting(writer, project);
            writer.WriteEndObject();

            foreach (var (name, value) in document.Preserved)
            {
                writer.WritePropertyName(name);
                if (value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    /// <exception cref="ProjectFormatException">The data is not a readable project.</exception>
    /// <exception cref="ProjectVersionException">The project was written by a newer version.</exception>
    public ProjectDocument Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw new ProjectFormatException("$", "the project file is too large.");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8Json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("$", $"the file is not valid JSON ({ex.Message}).", ex);
        }

        try
        {
            var root = Object(node, "$");
            if (root["format"] is not JsonValue format || !format.TryGetValue(out string? formatName) || formatName != FormatName)
            {
                throw new ProjectFormatException("$.format", $"must be \"{FormatName}\"; this is not a Cadence project.");
            }

            var version = Int(root, "formatVersion", "$", 0, int.MaxValue);
            if (version > _currentVersion)
            {
                throw new ProjectVersionException(version, _currentVersion);
            }

            while (version < _currentVersion)
            {
                if (!_migrations.TryGetValue(version, out var migration))
                {
                    throw new ProjectFormatException("$.formatVersion", string.Create(CultureInfo.InvariantCulture, $"format {version} is not supported."));
                }

                migration.Migrate(root);
                version++;
                root["formatVersion"] = version;
            }

            var project = ReadProject(Object(root, "project", "$"));
            var preserved = new JsonObject();
            foreach (var (name, value) in root.ToList())
            {
                if (!KnownRootProperties.Contains(name, StringComparer.Ordinal))
                {
                    root.Remove(name);
                    preserved[name] = value;
                }
            }

            return new ProjectDocument(project, preserved);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            // JsonNode reports structural problems such as duplicate property names this way.
            throw new ProjectFormatException("$", $"the file is malformed ({ex.Message}).", ex);
        }
    }

    private static void WriteSequence(Utf8JsonWriter writer, Sequence sequence)
    {
        writer.WriteStartObject("sequence");
        writer.WriteNumber("ppqn", sequence.Ppqn.TicksPerQuarterNote);

        writer.WriteStartArray("tempo");
        foreach (var change in sequence.TempoMap.Changes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", change.Position.Value);
            writer.WriteNumber("microsecondsPerQuarter", change.Tempo.MicrosecondsPerQuarterNote);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("meter");
        foreach (var change in sequence.MeterMap.Changes)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", change.Position.Value);
            writer.WriteNumber("numerator", change.Signature.Numerator);
            writer.WriteNumber("denominator", change.Signature.Denominator);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("markers");
        foreach (var marker in sequence.Markers)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", marker.Position.Value);
            writer.WriteString("name", marker.Name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("tracks");
        foreach (var track in sequence.Tracks)
        {
            writer.WriteStartObject();
            writer.WriteString("id", track.Id.Value.ToString("D"));
            writer.WriteString("name", track.Name);
            writer.WriteBoolean("muted", track.IsMuted);
            writer.WriteBoolean("soloed", track.IsSoloed);
            writer.WriteStartArray("events");
            foreach (var e in track.Events)
            {
                WriteEvent(writer, e);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEvent(Utf8JsonWriter writer, TrackEvent e)
    {
        writer.WriteStartObject();
        writer.WriteString("id", e.Id.Value.ToString("D"));
        writer.WriteNumber("tick", e.Position.Value);
        switch (e)
        {
            case NoteEvent note:
                writer.WriteString("type", "note");
                writer.WriteNumber("length", note.Duration.Value);
                writer.WriteNumber("channel", note.Channel.Number);
                writer.WriteNumber("note", note.Note.Value);
                writer.WriteNumber("velocity", note.Velocity.Value);
                writer.WriteNumber("release", note.ReleaseVelocity.Value);
                break;
            case ChannelEvent channel:
                Span<byte> bytes = stackalloc byte[3];
                var length = channel.Message.CopyTo(bytes);
                writer.WriteString("type", "channel");
                writer.WriteString("bytes", ToHex(bytes[..length]));
                break;
            case SysExEvent sysEx:
                writer.WriteString("type", "sysex");
                writer.WriteString("bytes", ToHex(sysEx.Message.Bytes.Span));
                break;
            case RawMidiEvent raw:
                writer.WriteString("type", "raw");
                writer.WriteString("bytes", ToHex(raw.Bytes.Span));
                break;
            case MetaEvent meta:
                writer.WriteString("type", "meta");
                writer.WriteNumber("metaType", meta.Type);
                writer.WriteString("bytes", ToHex(meta.Data.Span));
                break;
            default:
                throw new NotSupportedException($"Event type {e.GetType().Name} cannot be saved.");
        }

        writer.WriteEndObject();
    }

    private static void WriteRouting(Utf8JsonWriter writer, Project project)
    {
        // Routes for the project's tracks in track order, then any orphaned routes in ID order.
        var order = project.Sequence.Tracks.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var routes = project.Routing.Routes.Values
            .OrderBy(r => order.GetValueOrDefault(r.Track, int.MaxValue))
            .ThenBy(r => r.Track.Value);

        writer.WriteStartArray("routing");
        foreach (var route in routes)
        {
            writer.WriteStartObject();
            writer.WriteString("track", route.Track.Value.ToString("D"));
            if (route.Profile is { } profile)
            {
                writer.WriteStartObject("profile");
                writer.WriteString("id", profile.ProfileId);
                WriteOptional(writer, "name", profile.DisplayName);
                writer.WriteEndObject();
            }

            if (route.Endpoint is { } endpoint)
            {
                writer.WriteStartObject("endpoint");
                writer.WriteString("provider", endpoint.ProviderId);
                writer.WriteString("key", endpoint.EndpointKey);
                WriteOptional(writer, "name", endpoint.DisplayName);
                WriteOptional(writer, "manufacturer", endpoint.Manufacturer);
                WriteOptional(writer, "model", endpoint.Model);
                writer.WriteEndObject();
            }

            if (route.Channel is { } channel)
            {
                writer.WriteNumber("channel", channel.Number);
            }

            if (route.Transpose != 0)
            {
                writer.WriteNumber("transpose", route.Transpose);
            }

            if (route.Voice is { } voice)
            {
                writer.WriteStartObject("voice");
                writer.WriteString("bank", voice.BankId);
                writer.WriteNumber("program", voice.Program.Number);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    private static Project ReadProject(JsonObject json)
    {
        const string path = "$.project";
        var sequence = ReadSequence(Object(json, "sequence", path));
        TickRange? loop = null;
        if (OptionalObject(json, "loop", path) is { } loopJson)
        {
            var start = Long(loopJson, "start", $"{path}.loop", 0, long.MaxValue);
            var end = Long(loopJson, "end", $"{path}.loop", 0, long.MaxValue);
            loop = end > start ? new TickRange(new Tick(start), new Tick(end)) : throw new ProjectFormatException($"{path}.loop", "must end after it starts.");
        }

        return Guard($"{path}", () => new Project
        {
            Id = new ProjectId(Guid(json, "id", path)),
            Name = String(json, "name", path, Project.MaxNameLength),
            Sequence = sequence,
            Routing = ReadRouting(json, path),
            Loop = loop,
        });
    }

    private static Sequence ReadSequence(JsonObject json)
    {
        const string path = "$.project.sequence";
        var ppqn = new Ppqn(Int(json, "ppqn", path, 1, Ppqn.MaxValue));

        var tempo = Array(json, "tempo", path, 1_000_000).Select((node, i) =>
        {
            var at = $"{path}.tempo[{i}]";
            var item = Object(node, at);
            return new TempoChange(new Tick(Long(item, "tick", at, 0, long.MaxValue)), new Tempo(Int(item, "microsecondsPerQuarter", at, 1, Tempo.MaxMicrosecondsPerQuarterNote)));
        }).ToList();

        var meter = Array(json, "meter", path, 1_000_000).Select((node, i) =>
        {
            var at = $"{path}.meter[{i}]";
            var item = Object(node, at);
            var numerator = Int(item, "numerator", at, 1, 255);
            var denominator = Int(item, "denominator", at, 1, TimeSignature.MaxDenominator);
            var signature = Guard(at, () => new TimeSignature(numerator, denominator));
            return new MeterChange(new Tick(Long(item, "tick", at, 0, long.MaxValue)), signature);
        }).ToList();

        var markers = Array(json, "markers", path, 1_000_000).Select((node, i) =>
        {
            var at = $"{path}.markers[{i}]";
            var item = Object(node, at);
            return new Marker(new Tick(Long(item, "tick", at, 0, long.MaxValue)), String(item, "name", at, MaxNameLength));
        }).ToList();

        var tracks = Array(json, "tracks", path, MaxTracks).Select((node, i) => ReadTrack(Object(node, $"{path}.tracks[{i}]"), $"{path}.tracks[{i}]")).ToList();

        var tempoMap = new TempoMap(ppqn, tempo);
        var meterMap = Guard($"{path}.meter", () => new MeterMap(ppqn, meter));
        return Guard($"{path}.tracks", () => new Sequence(tempoMap, meterMap, tracks, markers));
    }

    private static Track ReadTrack(JsonObject json, string path)
    {
        var events = Array(json, "events", path, MaxEventsPerTrack).Select((node, i) => ReadEvent(Object(node, $"{path}.events[{i}]"), $"{path}.events[{i}]")).ToList();
        return Guard(path, () => new Track(
            new TrackId(Guid(json, "id", path)),
            String(json, "name", path, Track.MaxNameLength),
            events,
            Bool(json, "muted", path, false),
            Bool(json, "soloed", path, false)));
    }

    private static TrackEvent ReadEvent(JsonObject json, string path)
    {
        var id = new EventId(Guid(json, "id", path));
        var tick = new Tick(Long(json, "tick", path, 0, long.MaxValue));
        var type = String(json, "type", path, 16);
        return type switch
        {
            "note" => Guard(path, () => new NoteEvent(
                id,
                tick,
                new TickSpan(Long(json, "length", path, 1, long.MaxValue)),
                MidiChannel.FromNumber(Int(json, "channel", path, 1, 16)),
                new NoteNumber(Int(json, "note", path, 0, 127)),
                new Velocity(Int(json, "velocity", path, 1, 127)),
                new Velocity(Int(json, "release", path, 0, 127)))),
            "channel" => new ChannelEvent(id, tick, ReadChannelMessage(Hex(json, "bytes", path, 3), $"{path}.bytes")),
            "sysex" => new SysExEvent(id, tick, SysExMessage.TryCreate(Hex(json, "bytes", path, SysExMessage.MaxLength), out var message, out var error)
                ? message
                : throw new ProjectFormatException($"{path}.bytes", error)),
            "raw" => new RawMidiEvent(id, tick, ByteBlock.Copy(Hex(json, "bytes", path, RawMidiEvent.MaxLength))),
            "meta" => new MetaEvent(id, tick, (byte)Int(json, "metaType", path, 0, 127), ByteBlock.Copy(Hex(json, "bytes", path, MetaEvent.MaxLength))),
            _ => throw new ProjectFormatException($"{path}.type", $"\"{type}\" is not a known event type."),
        };
    }

    private static ChannelMessage ReadChannelMessage(byte[] bytes, string path)
    {
        if (bytes.Length >= 2
            && bytes.Length == 1 + ChannelMessage.DataLength(bytes[0])
            && ChannelMessage.TryCreate(bytes[0], bytes[1], bytes.Length == 3 ? bytes[2] : (byte)0, out var message))
        {
            return message;
        }

        throw new ProjectFormatException(path, "must be one complete MIDI channel message.");
    }

    private static RoutingTable ReadRouting(JsonObject project, string path)
    {
        var routes = Array(project, "routing", path, MaxTracks * 4).Select((node, i) =>
        {
            var at = $"{path}.routing[{i}]";
            var json = Object(node, at);
            ProfileReference? profile = null;
            if (OptionalObject(json, "profile", at) is { } p)
            {
                profile = Guard($"{at}.profile", () => new ProfileReference(String(p, "id", $"{at}.profile", 100), OptionalString(p, "name", $"{at}.profile", MaxNameLength)));
            }

            EndpointReference? endpoint = null;
            if (OptionalObject(json, "endpoint", at) is { } e)
            {
                var ep = $"{at}.endpoint";
                endpoint = Guard(ep, () => new EndpointReference(
                    String(e, "provider", ep, 256),
                    String(e, "key", ep, 256),
                    OptionalString(e, "name", ep, MaxNameLength),
                    OptionalString(e, "manufacturer", ep, MaxNameLength),
                    OptionalString(e, "model", ep, MaxNameLength)));
            }

            VoiceAssignment? voice = null;
            if (OptionalObject(json, "voice", at) is { } v)
            {
                voice = new VoiceAssignment(String(v, "bank", $"{at}.voice", 64), ProgramNumber.FromNumber(Int(v, "program", $"{at}.voice", 1, 128)));
            }

            return new TrackRoute(new TrackId(Guid(json, "track", at)))
            {
                Profile = profile,
                Endpoint = endpoint,
                Channel = OptionalInt(json, "channel", at, 1, 16) is { } channel ? MidiChannel.FromNumber(channel) : null,
                Transpose = OptionalInt(json, "transpose", at, -TrackRoute.MaxTranspose, TrackRoute.MaxTranspose) ?? 0,
                Voice = voice,
            };
        }).ToList();

        return Guard($"{path}.routing", () => RoutingTable.From(routes));
    }

    /// <summary>Converts domain validation failures into format errors at <paramref name="path"/>.</summary>
    private static T Guard<T>(string path, Func<T> create)
    {
        try
        {
            return create();
        }
        catch (ArgumentException ex)
        {
            throw new ProjectFormatException(path, ex.Message, ex);
        }
    }
}
