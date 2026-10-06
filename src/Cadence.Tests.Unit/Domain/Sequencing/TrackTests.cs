using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using CsCheck;

namespace Cadence.Tests.Unit.Domain.Sequencing;

public sealed class TrackTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, int note = 60, long length = 10) =>
        new(new Tick(at), new TickSpan(length), One, new NoteNumber(note), new Velocity(100));

    private static ControllerEvent Cc(long at, ControllerNumber controller, int value = 0) =>
        new(new Tick(at), One, controller, ControlValue.FromSevenBit(value));

    private static ProgramEvent Program(long at, int program) =>
        new(new Tick(at), One, new ProgramSelection(new ProgramNumber(program)));

    [Fact]
    public void SimultaneousEvents_FollowCanonicalPhases()
    {
        var note = Note(0);
        var program = Program(0, 5);
        var bankLsb = Cc(0, ControllerNumber.BankSelectLsb);
        var volume = Cc(0, ControllerNumber.ChannelVolume, 100);
        var bankMsb = Cc(0, ControllerNumber.BankSelectMsb);
        var sysEx = new SysExEvent(Tick.Zero, SysExMessage.Create([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]));
        var meta = new MetaEvent(EventId.New(), Tick.Zero, 0x01, ByteBlock.Copy("hi"u8));

        var track = new Track(TrackId.New(), "t", [note, program, bankLsb, volume, bankMsb, sysEx, meta]);

        Assert.Equal<TrackEvent>([meta, sysEx, bankLsb, bankMsb, program, volume, note], track.Events);
    }

    [Fact]
    public void Ties_PreserveSuppliedOrder()
    {
        // An RPN sequence must reach the device in the order written.
        var rpnMsb = Cc(0, ControllerNumber.RpnMsb);
        var rpnLsb = Cc(0, ControllerNumber.RpnLsb);
        var data = Cc(0, ControllerNumber.DataEntryMsb, 2);

        var track = new Track(TrackId.New(), "t", [rpnMsb, rpnLsb, data]);

        Assert.Equal<TrackEvent>([rpnMsb, rpnLsb, data], track.Events);
    }

    [Fact]
    public void Add_InsertsAfterTies()
    {
        var first = Cc(10, ControllerNumber.ModulationWheel, 1);
        var second = Cc(10, ControllerNumber.ModulationWheel, 2);
        var track = new Track(TrackId.New(), "t", [Note(0), first, Note(20)]).Add(second);

        Assert.Equal(1, track.Events.IndexOf(first));
        Assert.Equal(2, track.Events.IndexOf(second));
    }

    [Fact]
    public void Replace_RepositionsEvent()
    {
        var moving = Note(0);
        var track = new Track(TrackId.New(), "t", [moving, Note(50)]);

        var moved = moving with { Position = new Tick(100) };
        var updated = track.Replace(moved);

        Assert.Same(moved, updated.Events[^1]);
        Assert.Equal(2, updated.Events.Length);
        Assert.Equal(new Tick(110), updated.EndPosition);
        Assert.Throws<KeyNotFoundException>(() => track.Replace(Note(5)));
    }

    [Fact]
    public void Remove_IgnoresUnknownId()
    {
        var note = Note(0);
        var track = new Track(TrackId.New(), "t", [note]);

        Assert.Empty(track.Remove(note.Id).Events);
        Assert.Same(track, track.Remove(EventId.New()));
    }

    [Fact]
    public void DuplicateEventIds_AreRejected()
    {
        var note = Note(0);

        Assert.Throws<ArgumentException>(() => new Track(TrackId.New(), "t", [note, note with { Position = new Tick(5) }]));
        Assert.Throws<ArgumentException>(() => new Track(TrackId.New(), "t", [note]).Add(note));
    }

    [Fact]
    public void Names_AreBounded() =>
        Assert.Throws<ArgumentException>(() => Track.Create(new string('x', Track.MaxNameLength + 1)));

    [Fact]
    public void Edits_DoNotMutateTheOriginal()
    {
        var original = Track.Create("t");
        var edited = original.Add(Note(0)).WithName("u").WithMuted(true).WithSoloed(true);

        Assert.Empty(original.Events);
        Assert.Equal("t", original.Name);
        Assert.False(original.IsMuted);
        Assert.Equal(("u", true, true, original.Id), (edited.Name, edited.IsMuted, edited.IsSoloed, edited.Id));
    }

    [Fact]
    public void NoteEvent_EnforcesInvariantsIncludingThroughWith()
    {
        var note = Note(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => Note(0, length: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => note with { Duration = TickSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => note with { Velocity = Velocity.Off });
        Assert.Equal(new Tick(10), note.EndPosition);
    }

    [Fact]
    public void ChannelEvents_FallInTheirDocumentedPhases()
    {
        Assert.Equal(EventPhase.NoteOff, new NoteOffEvent(EventId.New(), Tick.Zero, One, NoteNumber.MiddleC, Velocity.DefaultRelease).Phase);
        Assert.Equal(EventPhase.BankSelect, Cc(0, ControllerNumber.BankSelectLsb).Phase);
        Assert.Equal(EventPhase.Control, Cc(0, ControllerNumber.ChannelVolume).Phase);
        Assert.Equal(EventPhase.ProgramChange, Program(0, 1).Phase);
        Assert.Equal(EventPhase.Control, new PitchBendEvent(Tick.Zero, One, ControlValue.Center).Phase);
    }

    [Fact]
    public void MetaEvent_RejectsInvalidType() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetaEvent(EventId.New(), Tick.Zero, 0x80, ByteBlock.Empty));

    private static readonly Gen<TrackEvent> GenEvent =
        Gen.OneOf<TrackEvent>(
            Gen.Select(Gen.Long[0, 20], Gen.Int[0, 127]).Select(x => (TrackEvent)Note(x.Item1, x.Item2)),
            Gen.Select(Gen.Long[0, 20], Gen.Int[0, 127]).Select(x => (TrackEvent)Cc(x.Item1, new ControllerNumber(x.Item2))),
            Gen.Select(Gen.Long[0, 20], Gen.Int[0, 127]).Select(x => (TrackEvent)Program(x.Item1, x.Item2)));

    [Fact]
    public void Events_AreAStableSortOfTheInput() =>
        GenEvent.Array[0, 40].Sample(events =>
        {
            var track = new Track(TrackId.New(), "t", events);
            var expected = events
                .Select((e, index) => (e, index))
                .OrderBy(x => x.e.Position).ThenBy(x => x.e.Phase).ThenBy(x => x.index)
                .Select(x => x.e);
            return track.Events.SequenceEqual(expected);
        });

    [Fact]
    public void Add_MatchesConstructingFromAllEvents() =>
        GenEvent.Array[0, 30].Sample(events =>
        {
            var incremental = events.Aggregate(Track.Create("t"), (track, e) => track.Add(e));
            return incremental.Events.SequenceEqual(new Track(TrackId.New(), "t", events).Events);
        });
}
