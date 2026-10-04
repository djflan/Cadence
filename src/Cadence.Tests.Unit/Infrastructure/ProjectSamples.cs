using Cadence.Domain.Midi;
using Cadence.Domain.Projects;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Tests.Unit.Infrastructure;

internal static class ProjectSamples
{
    /// <summary>A project exercising every event type, conductor data, routing options, and a loop.</summary>
    public static Project Full()
    {
        var ppqn = new Ppqn(480);
        var channel = MidiChannel.FromNumber(2);
        var bass = new Track(TrackId.New(), "Bass", [
            new NoteEvent(EventId.New(), new Tick(0), new TickSpan(240), channel, new NoteNumber(40), new Velocity(100), new Velocity(30)),
            new ChannelEvent(new Tick(0), ChannelMessage.ProgramChange(channel, new ProgramNumber(33))),
            new ChannelEvent(new Tick(10), ChannelMessage.PitchBend(channel, new FourteenBitValue(9000))),
            new ChannelEvent(new Tick(12), ChannelMessage.ChannelPressure(channel, new SevenBitValue(5))),
            new SysExEvent(new Tick(0), SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7])),
            new RawMidiEvent(EventId.New(), new Tick(20), ByteBlock.Copy([0xF0, 0x43])),
            new MetaEvent(EventId.New(), new Tick(30), 0x05, ByteBlock.Copy("la"u8)),
        ], isMuted: true);
        var drums = new Track(TrackId.New(), "Drüms ♪", [], isSoloed: true);
        var sequence = new Sequence(
            new TempoMap(ppqn, [new TempoChange(new Tick(960), new Tempo(400_000))]),
            new MeterMap(ppqn, [new MeterChange(new Tick(1920), new TimeSignature(7, 8))]),
            [bass, drums],
            [new Marker(new Tick(480), "Verse \"A\"")]);

        return new Project
        {
            Id = ProjectId.New(),
            Name = "Demo",
            Sequence = sequence,
            Loop = new TickRange(Tick.Zero, new Tick(1920)),
            Routing = RoutingTable.From([
                new TrackRoute(bass.Id)
                {
                    Profile = new ProfileReference("cadence.generic.xg", "Generic XG"),
                    Endpoint = new EndpointReference("coremidi", "-12345", "QY Out", "Maker", "Model"),
                    Channel = MidiChannel.FromNumber(3),
                    Transpose = -12,
                    Voice = new VoiceAssignment("normal", ProgramNumber.FromNumber(34)),
                },
                new TrackRoute(TrackId.New()) { Endpoint = new EndpointReference("loopback", "orphan") },
            ]),
        };
    }

    /// <summary>A comparable description of a project, ignoring nothing that should persist.</summary>
    public static string Describe(Project project)
    {
        var s = project.Sequence;
        var lines = new List<string>
        {
            $"{project.Id} {project.Name} loop={project.Loop?.Start}-{project.Loop?.End} ppqn={s.Ppqn}",
            "tempo " + string.Join(",", s.TempoMap.Changes),
            "meter " + string.Join(",", s.MeterMap.Changes),
            "markers " + string.Join(",", s.Markers),
        };
        foreach (var track in s.Tracks)
        {
            lines.Add($"track {track.Id} {track.Name} m={track.IsMuted} s={track.IsSoloed}");
            lines.AddRange(track.Events.Select(e => e switch
            {
                SysExEvent x => $"  {x.Id} {x.Position} sysex {Convert.ToHexString(x.Message.Bytes.Span)}",
                RawMidiEvent x => $"  {x.Id} {x.Position} raw {Convert.ToHexString(x.Bytes.Span)}",
                MetaEvent x => $"  {x.Id} {x.Position} meta {x.Type} {Convert.ToHexString(x.Data.Span)}",
                _ => "  " + e,
            }));
        }

        lines.AddRange(project.Routing.Routes.Values.OrderBy(r => r.Track.Value).Select(r => $"route {r}"));
        return string.Join("\n", lines);
    }
}
