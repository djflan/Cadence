using System.Text.Json.Nodes;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using static Cadence.Infrastructure.Projects.NodeReader;

namespace Cadence.Infrastructure.Projects;

/// <summary>
/// Format 3 puts a track's events in clips (ADR 0020). Each format 2 track's events move into one note
/// clip from the bar of the first event to the bar line after the latest end, with ticks relative to
/// the clip, so the track plays exactly as before. The clip takes the track's ID. A track with no
/// events gets no clip.
/// </summary>
internal sealed class Format2To3Migration : IProjectMigration
{
    private const string SequencePath = "$.project.sequence";

    public int FromVersion => 2;

    public void Migrate(JsonObject root)
    {
        var sequence = Object(Object(root, "project", "$"), "sequence", "$.project");
        var meter = ReadMeter(sequence);
        var tracks = Array(sequence, "tracks", SequencePath, int.MaxValue);
        for (var t = 0; t < tracks.Count; t++)
        {
            var path = $"{SequencePath}.tracks[{t}]";
            var track = Object(tracks[t], path);
            var events = Array(track, "events", path, int.MaxValue);
            track.Remove("events");
            var clips = new JsonArray();
            track["clips"] = clips;
            if (events.Count == 0)
            {
                continue;
            }

            long first = long.MaxValue, last = 0, latestEnd = 0;
            for (var i = 0; i < events.Count; i++)
            {
                var at = $"{path}.events[{i}]";
                var e = Object(events[i], at);
                var tick = Long(e, "tick", at, 0, long.MaxValue);
                var end = String(e, "type", at, 16) == "note" ? checked(tick + Long(e, "length", at, 1, long.MaxValue)) : tick;
                first = Math.Min(first, tick);
                last = Math.Max(last, tick);
                latestEnd = Math.Max(latestEnd, end);
            }

            var (start, clipEnd) = NoteClip.EnclosingBounds(first, last, latestEnd, meter);
            foreach (var node in events)
            {
                var e = node!.AsObject();
                e["tick"] = e["tick"]!.GetValue<long>() - start.Value;
            }

            clips.Add(new JsonObject
            {
                ["id"] = String(track, "id", path, 36),
                ["type"] = "note",
                ["start"] = start.Value,
                ["length"] = clipEnd.Value - start.Value,
                ["offset"] = 0L,
                ["events"] = events,
            });
        }
    }

    private static MeterMap ReadMeter(JsonObject sequence)
    {
        var ppqn = Int(sequence, "ppqn", SequencePath, 1, Ppqn.MaxValue);
        var changes = Array(sequence, "meter", SequencePath, int.MaxValue).Select((node, i) =>
        {
            var at = $"{SequencePath}.meter[{i}]";
            var item = Object(node, at);
            return (Tick: Long(item, "tick", at, 0, long.MaxValue), Numerator: Int(item, "numerator", at, 1, 255), Denominator: Int(item, "denominator", at, 1, TimeSignature.MaxDenominator));
        }).ToList();

        try
        {
            return new MeterMap(new Ppqn(ppqn), changes.Select(c => new MeterChange(new Tick(c.Tick), new TimeSignature(c.Numerator, c.Denominator))));
        }
        catch (ArgumentException ex)
        {
            throw new ProjectFormatException($"{SequencePath}.meter", ex.Message, ex);
        }
    }
}
