using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Playback;
using static Cadence.Tests.Unit.Playback.PlaybackFixture;

namespace Cadence.Tests.Unit.Playback;

public sealed class PlaybackPlanCompilerTests
{
    private static PlaybackPlan Compile(Sequence sequence, params (Track Track, PlanTrackBinding Binding)[] bindings) =>
        PlaybackPlanCompiler.Compile(sequence, bindings.ToDictionary(b => b.Track.Id, b => b.Binding));

    private static Sequence With(params Track[] tracks) => tracks.Aggregate(Sequence.CreateEmpty(Resolution), (s, t) => s.WithTrack(t));

    [Fact]
    public void MutedTracksAreSkipped_SoloOverridesMute()
    {
        var muted = new Track(TrackId.New(), "m", [Note(0, 10)], isMuted: true);
        var plain = new Track(TrackId.New(), "p", [Note(0, 10)]);
        var soloMuted = new Track(TrackId.New(), "s", [Note(0, 10)], isMuted: true, isSoloed: true);

        Assert.Equal(1, Compile(With(muted, plain), (muted, new(0)), (plain, new(0))).EventCount);
        Assert.Equal(1, Compile(With(muted, plain, soloMuted), (muted, new(0)), (plain, new(0)), (soloMuted, new(0))).EventCount);
    }

    [Fact]
    public void UnboundTracks_AreReported()
    {
        var track = new Track(TrackId.New(), "t", [Note(0, 10)]);

        var plan = Compile(With(track));

        Assert.Equal(0, plan.EventCount);
        Assert.Equal(track.Id, Assert.Single(plan.Diagnostics).Track);
    }

    [Fact]
    public void ChannelOverride_ReaddressesEveryChannelMessage()
    {
        var track = new Track(TrackId.New(), "t", [Note(0, 10), Cc(0, 7, 1, channel: 3)]);

        var plan = Compile(With(track), (track, new(0, MidiChannel.FromNumber(10))));

        Assert.All(plan.Events, e => Assert.Equal(9, e.Message.Channel.Index));
    }

    [Fact]
    public void Events_AreOrderedByTickPhaseTrackThenPosition()
    {
        var a = new Track(TrackId.New(), "a", [Note(0, 10), Cc(0, 7, 1)]);
        var b = new Track(TrackId.New(), "b", [Cc(0, 7, 2), Program(0, 1)]);

        var plan = Compile(With(a, b), (a, new(0)), (b, new(1)));

        Assert.Equal(["C0 1 1", "B0 0 7", "B0 1 7", "90 0 3C"], plan.Events.Select(e => $"{e.Message.Status:X2} {e.Slot} {e.Message.Data1:X}"));
    }

    [Fact]
    public void RawEvents_AreSentWhenCompleteOrJoinedWhenSplit()
    {
        var track = new Track(TrackId.New(), "t", [
            Raw(0, 0xF8),
            Raw(10, 0xF0, 0x43, 0x10),
            Raw(12, 0x4C, 0x00, 0xF7),
            Raw(20, 0x4C),
        ]);

        var plan = Compile(With(track), (track, new(0)));

        Assert.Equal([0L, 10L], plan.Events.Select(e => e.Tick));
        Assert.Equal([0xF0, 0x43, 0x10, 0x4C, 0x00, 0xF7], plan.Payloads[plan.Events[1].PayloadIndex].ToArray());
        Assert.Single(plan.Diagnostics);
    }

    [Fact]
    public void UnterminatedSplitSysEx_IsReportedNotSent()
    {
        var track = new Track(TrackId.New(), "t", [Raw(0, 0xF0, 0x43), Raw(1, 0x10)]);

        var plan = Compile(With(track), (track, new(0)));

        Assert.Equal(0, plan.EventCount);
        Assert.Single(plan.Diagnostics);
    }

    [Fact]
    public void MetaEvents_AreNotTransmitted()
    {
        var track = new Track(TrackId.New(), "t", [new MetaEvent(EventId.New(), Tick.Zero, 1, ByteBlock.Copy("x"u8))]);

        Assert.Equal(0, Compile(With(track), (track, new(0))).EventCount);
    }

    private static RawMidiEvent Raw(long tick, params byte[] bytes) => new(EventId.New(), new Tick(tick), ByteBlock.Copy(bytes));
}
