using Cadence.Application.Routing;
using Cadence.Domain.Midi;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Midi.Wire;
using Cadence.Playback;
using Cadence.Profiles;
using static Cadence.Tests.Unit.Application.RouteResolverTests;

namespace Cadence.Tests.Unit.Application;

public sealed class PlaybackRoutingTests
{
    private static readonly MidiChannel One = MidiChannel.FromIndex(0);

    private static NoteEvent Note(long at, int note) => new(new Tick(at), new TickSpan(10), One, new NoteNumber(note), Velocity.Max);

    [Fact]
    public void Prepare_SharesSlotsPerEndpointAndSkipsUnplayableTracks()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var c = Track.Create("c");
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(a).WithTrack(b).WithTrack(c);
        var synth = Output("loopback", "s", "Synth");
        var routes = RoutingTable.From([
            new TrackRoute(a.Id) { Endpoint = RouteResolver.ReferenceTo(synth) },
            new TrackRoute(b.Id) { Endpoint = RouteResolver.ReferenceTo(synth), Channel = MidiChannel.FromNumber(2) },
            new TrackRoute(c.Id) { Endpoint = new EndpointReference("loopback", "gone", "Gone") },
        ]);

        var prepared = PlaybackRouting.Prepare(sequence, RouteResolver.ResolveAll(sequence, routes, ProfileCatalog.Empty, [synth]));

        Assert.Equal([synth.Id], prepared.Slots);
        Assert.Equal(0, prepared.Bindings[a.Id].OutputSlot);
        Assert.Equal(MidiChannel.FromNumber(2), prepared.Bindings[b.Id].Channel);
        Assert.False(prepared.Bindings.ContainsKey(c.Id));
    }

    [Fact]
    public void Prepare_RendersVoiceSelectionFromTheProfile()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 60)]);
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track);
        var synth = Output("loopback", "s", "Synth");
        var route = new TrackRoute(track.Id)
        {
            Endpoint = RouteResolver.ReferenceTo(synth),
            Profile = new ProfileReference("p.one"),
            Voice = new VoiceAssignment("main", ProgramNumber.FromNumber(5)),
            Channel = MidiChannel.FromNumber(3),
        };

        var prepared = PlaybackRouting.Prepare(sequence, RouteResolver.ResolveAll(sequence, RoutingTable.Empty.With(route), Catalog("p.one"), [synth]));

        var voice = Assert.IsType<ProgramEvent>(Assert.Single(prepared.Bindings[track.Id].InitialEvents));
        Assert.Equal(["B2 0 0", "B2 32 3", "C2 4 0"], Midi1Encoder.Encode(voice).Select(m => $"{m.Status:X2} {m.Data1} {m.Data2}"));
    }

    [Fact]
    public void Compile_PlacesVoiceSelectionBeforeTheTracksOwnEvents()
    {
        var program = new ProgramEvent(Tick.Zero, One, new ProgramSelection(new ProgramNumber(9)));
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 60), program]);
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track);
        var binding = new PlanTrackBinding(0) { InitialEvents = [new ProgramEvent(Tick.Zero, One, new ProgramSelection(new ProgramNumber(2), BankMsb: new SevenBitValue(1)))] };

        var plan = PlaybackPlanCompiler.Compile(sequence, new Dictionary<TrackId, PlanTrackBinding> { [track.Id] = binding });

        Assert.Equal(["B0 0", "C0 2", "C0 9", "90 3C"], plan.Events.Select(e => $"{e.Message.Status:X2} {e.Message.Data1:X}"));
    }

    [Fact]
    public void Compile_TransposesNotesAndDropsThoseOutOfRange()
    {
        var rawOff = new NoteOffEvent(EventId.New(), new Tick(5), One, new NoteNumber(100), Velocity.DefaultRelease);
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0, 60), Note(1, 120), rawOff]);
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(track);

        var plan = PlaybackPlanCompiler.Compile(sequence, new Dictionary<TrackId, PlanTrackBinding> { [track.Id] = new(0, null, 12) });

        Assert.Equal([72, 112], plan.Events.Select(e => (int)e.Message.Data1));
        Assert.Contains("1 notes were transposed outside", Assert.Single(plan.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_RoutedTrackPlaysThroughTheLoopbackWithItsVoice()
    {
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        var port = provider.CreatePort("Module", "m");
        using var directory = new EndpointDirectory([provider]);

        var track = Track.FromEvents(TrackId.New(), "Bass", [Note(0, 40)]);
        var sequence = Sequence.CreateEmpty(new Ppqn(500)).WithTrack(track);
        var route = new TrackRoute(track.Id)
        {
            Endpoint = RouteResolver.ReferenceTo(directory.GetEndpoints(EndpointDirection.Output).Single()),
            Profile = new ProfileReference("p.one"),
            Voice = new VoiceAssignment("main", ProgramNumber.FromNumber(34)),
        };

        var resolved = RouteResolver.ResolveAll(sequence, RoutingTable.Empty.With(route), Catalog("p.one"), directory.GetEndpoints());
        var prepared = PlaybackRouting.Prepare(sequence, resolved);
        using var outputs = await PlaybackRouting.OpenAsync(prepared, directory, TestContext.Current.CancellationToken);
        using var engine = new PlaybackEngine(clock, sequence.TempoMap);
        engine.SetOutputs(outputs.Outputs);
        engine.Load(PlaybackPlanCompiler.Compile(sequence, prepared.Bindings));
        engine.Play(Tick.Zero);
        engine.Pump();

        Assert.Empty(outputs.Problems);
        Assert.Equal(["B00000", "B02003", "C021", "B00000", "B02003", "C021", "90287F", "802840"], port.Sent.Select(m => Convert.ToHexString(m.Bytes)));
    }

    [Fact]
    public async Task OpenAsync_ReportsEndpointsThatVanishedSinceResolution()
    {
        using var provider = new LoopbackMidiProvider(new VirtualClock());
        var port = provider.CreatePort("Gone soon", "g");
        using var directory = new EndpointDirectory([provider]);
        var prepared = new PreparedRouting([port.OutputId], System.Collections.Immutable.ImmutableDictionary<TrackId, PlanTrackBinding>.Empty);
        provider.RemovePort("g");

        using var outputs = await PlaybackRouting.OpenAsync(prepared, directory, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(outputs.Outputs));
        Assert.Single(outputs.Problems);
    }
}
