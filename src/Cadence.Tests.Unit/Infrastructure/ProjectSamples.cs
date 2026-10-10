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
        var bass = Track.FromEvents(TrackId.New(), "Bass", [
            new NoteEvent(EventId.New(), new Tick(0), new TickSpan(240), channel, new NoteNumber(40), new Velocity(100), new Velocity(30)),
            new ProgramEvent(new Tick(0), channel, new ProgramSelection(new ProgramNumber(33), new SevenBitValue(0), new SevenBitValue(64))),
            new PitchBendEvent(new Tick(10), channel, ControlValue.FromFourteenBit(9000)),
            new ChannelPressureEvent(new Tick(12), channel, ControlValue.FromSevenBit(5)),
            new ControllerEvent(new Tick(12), channel, ControllerNumber.ChannelVolume, new ControlValue(0x1234_5678)),
            new PolyPressureEvent(EventId.New(), new Tick(14), channel, NoteNumber.MiddleC, ControlValue.Max),
            new NoteOffEvent(EventId.New(), new Tick(16), channel, NoteNumber.MiddleC, new Velocity(3)),
            new SysExEvent(new Tick(0), SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7])),
            new RawMidiEvent(EventId.New(), new Tick(20), ByteBlock.Copy([0xF0, 0x43])),
            new MetaEvent(EventId.New(), new Tick(30), 0x05, ByteBlock.Copy("la"u8)),
        ], isMuted: true);
        var drums = Track.FromEvents(TrackId.New(), "Drüms ♪", [], isSoloed: true);
        // Two clips, one trimmed and named, with content hidden on both sides.
        var lead = new Track(TrackId.New(), "Lead", [
            new NoteClip(ClipId.New(), new Tick(1920), new TickSpan(960), new TickSpan(240), new EventList([
                new NoteEvent(new Tick(0), new TickSpan(120), channel, new NoteNumber(72), new Velocity(90)),
                new NoteEvent(new Tick(480), new TickSpan(2000), channel, new NoteNumber(74), new Velocity(91)),
                new ControllerEvent(new Tick(1300), channel, ControllerNumber.ModulationWheel, ControlValue.Max),
            ]), "Hook"),
            new NoteClip(ClipId.New(), new Tick(3840), new TickSpan(480), TickSpan.Zero, new EventList([
                new NoteEvent(new Tick(0), new TickSpan(240), channel, new NoteNumber(76), new Velocity(92)),
            ])),
        ]);
        var sequence = new Sequence(
            new TempoMap(ppqn, [new TempoChange(new Tick(960), new Tempo(400_000))]),
            new MeterMap(ppqn, [new MeterChange(new Tick(1920), new TimeSignature(7, 8))]),
            [bass, drums, lead],
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
            foreach (var clip in track.Clips.Cast<NoteClip>())
            {
                lines.Add($"  clip {clip.Id} {clip.Start}+{clip.Length} offset={clip.ContentOffset} \"{clip.Name}\"");
                lines.AddRange(clip.Content.Items.Select(e => e switch
                {
                    SysExEvent x => $"    {x.Id} {x.Position} sysex {Convert.ToHexString(x.Message.Bytes.Span)}",
                    RawMidiEvent x => $"    {x.Id} {x.Position} raw {Convert.ToHexString(x.Bytes.Span)}",
                    MetaEvent x => $"    {x.Id} {x.Position} meta {x.Type} {Convert.ToHexString(x.Data.Span)}",
                    _ => "    " + e,
                }));
            }
        }

        lines.AddRange(project.Routing.Routes.Values.OrderBy(r => r.Track.Value).Select(r => $"route {r}"));
        return string.Join("\n", lines);
    }
}
