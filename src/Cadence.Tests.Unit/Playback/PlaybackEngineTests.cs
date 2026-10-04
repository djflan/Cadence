using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Playback;
using static Cadence.Tests.Unit.Playback.PlaybackFixture;

namespace Cadence.Tests.Unit.Playback;

public sealed class PlaybackEngineTests
{
    private const EndpointCapabilities Immediate = EndpointCapabilities.SystemExclusive;

    [Fact]
    public void ScheduledOutput_ReceivesEventsWithinLookAheadWithTimestamps()
    {
        using var f = new PlaybackFixture();
        f.Load(Note(0, 100), Note(10, 5, 62), Note(50, 5, 64));
        f.Engine.Play(Tick.Zero);

        f.PumpAt(0);
        Assert.Equal(["903C64@0", "903E64@10", "803E40@15"], f.Sent());

        f.PumpAt(40);
        Assert.Equal(["903C64@0", "903E64@10", "803E40@15", "904064@50", "804040@55"], f.Sent());

        f.PumpAt(80);
        Assert.Equal("803C40@100", f.Sent()[^1]);
    }

    [Fact]
    public void ImmediateOutput_SendsOnlyWhenDue()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 100), Note(10, 5, 62));
        f.Engine.Play(Tick.Zero);

        var wait = f.PumpAt(0);
        Assert.Equal(["903C64@0"], f.Sent());
        Assert.Equal(TimeSpan.FromMilliseconds(10), wait);

        f.PumpAt(9.9);
        Assert.Single(f.Sent());

        f.PumpAt(10.5);
        Assert.Equal(["903C64@0", "903E64@10.5"], f.Sent());
        Assert.True(f.Port.Sent.All(m => m.Requested.IsImmediate));
    }

    [Fact]
    public void SimultaneousEvents_ReleaseBeforeStateChangesBeforeRetrigger()
    {
        using var f = new PlaybackFixture();
        f.Load(Note(0, 10), Note(10, 10), Program(10, 5), Cc(10, 0, 1), Cc(10, 7, 90));
        f.Engine.Play(Tick.Zero);

        f.PumpAt(0);

        Assert.Equal(["903C64@0", "803C40@10", "B00001@10", "C005@10", "B0075A@10", "903C64@10", "803C40@20"], f.Sent());
    }

    [Fact]
    public void Stop_ReleasesSoundingNotesAndSustain()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Cc(0, 64, 127), Note(0, 1000));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);

        f.Engine.Stop();
        f.PumpAt(5);

        Assert.Equal(["B0407F@0", "903C64@0", "803C40@5", "B04000@5"], f.Sent());
        Assert.Equal(TransportState.Stopped, f.Engine.State);
        Assert.Equal(new Tick(5), f.Engine.Position);
        Assert.Equal(Timeout.InfiniteTimeSpan, f.PumpAt(6));
    }

    [Fact]
    public void Stop_NeverReleasesAScheduledNoteBeforeItStarts()
    {
        using var f = new PlaybackFixture();
        f.Load(Note(15, 100));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);

        f.Engine.Stop();
        f.PumpAt(1);

        Assert.Equal(["903C64@15", "803C40@15"], f.Sent());
    }

    [Fact]
    public void Panic_SweepsEveryChannelOfEveryOutput()
    {
        using var f = new PlaybackFixture(null, Immediate, Immediate);
        f.Load(Note(0, 1000));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);

        f.Engine.Panic();
        f.PumpAt(1);

        var first = f.Sent(0);
        Assert.Equal("803C40@1", first[1]);
        Assert.Equal(1 + 1 + (16 * 3), first.Count);
        Assert.Equal(16 * 3, f.Sent(1).Count);
        Assert.Contains("BF7B00@1", f.Sent(1));
        Assert.Contains("BF7800@1", f.Sent(1));
        Assert.Equal(TransportState.Playing, f.Engine.State);
    }

    [Fact]
    public void Play_ChasesControllersButNotNotesOrSystemExclusive()
    {
        using var f = new PlaybackFixture();
        f.Load(
            new SysExEvent(Tick.Zero, SysExMessage.Create([0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7])),
            Cc(0, 7, 80),
            Cc(10, 7, 100),
            Cc(10, 32, 3),
            Cc(10, 0, 1),
            Program(20, 9),
            Cc(30, 101, 0),
            new ChannelEvent(new Tick(40), ChannelMessage.PitchBend(One, new FourteenBitValue(9000))),
            Note(50, 2000),
            Note(1100, 10));

        f.Engine.Play(new Tick(1000));
        f.PumpAt(0);

        Assert.Equal(["B00001@0", "B02003@0", "C009@0", "B00764@0", "E02846@0"], f.Sent()[..5]);
        Assert.DoesNotContain(f.Sent(), m => m.StartsWith("F0", StringComparison.Ordinal) || m.StartsWith("90", StringComparison.Ordinal));
    }

    [Fact]
    public void Loop_WrapsAndReleasesNotesCrossingTheLoopEnd()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 150), Note(50, 10, 62), Note(200, 10, 64));
        f.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(100)));
        f.Engine.Play(Tick.Zero);

        foreach (var ms in new[] { 0, 50, 60, 100, 150, 160, 200, 250 })
        {
            f.PumpAt(ms);
        }

        Assert.Equal(
            ["903C64@0", "903E64@50", "803E40@60", "803C40@100", "903C64@100", "903E64@150", "803E40@160", "803C40@200", "903C64@200", "903E64@250"],
            f.Sent());
        Assert.Equal(new Tick(50), f.Engine.Position);
    }

    [Fact]
    public void Loop_DoesNotEngageWhenStartingPastItsEnd()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 10), Note(150, 10));
        f.Engine.SetLoop(new LoopRegion(Tick.Zero, new Tick(100)));
        f.Engine.Play(new Tick(120));

        f.PumpAt(0);
        f.PumpAt(30);

        Assert.Equal(["903C64@30"], f.Sent());
    }

    [Fact]
    public void LoadDuringPlayback_KeepsSoundingNotesAndSkipsWhatAlreadyPlayed()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 300), Cc(5, 7, 1));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);
        f.PumpAt(10);

        // The edit removes the sounding note and adds two new events, one already in the past.
        f.Load(Cc(5, 7, 2), Note(200, 10, 62));
        f.PumpAt(200);
        f.PumpAt(300);

        Assert.Equal(["903C64@0", "B00701@10", "903E64@200", "803E40@300", "803C40@300"], f.Sent());
    }

    [Fact]
    public void LoadDuringPlayback_AppliesTempoChangesFromThePlayhead()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(100, 10), Note(200, 10, 62));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);
        f.PumpAt(100);

        var halfSpeed = TempoMap.Constant(Resolution, new Tempo(1_000_000));
        f.Engine.Load(Plan(halfSpeed, Note(100, 10), Note(200, 10, 62)));
        f.PumpAt(110);
        f.PumpAt(150);
        f.PumpAt(300);

        Assert.Equal(["903C64@100", "803C40@110", "903E64@300"], f.Sent());
    }

    [Fact]
    public void LateNotes_AreSkippedButStateMessagesAreKept()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 10), Cc(1, 7, 50), Note(50, 10, 64), Note(400, 10, 62));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);

        // The playback thread stalls for 400 ms.
        f.PumpAt(400);

        Assert.Equal(["903C64@0", "B00732@400", "803C40@400", "903E64@400"], f.Sent());
        var stats = f.Engine.Statistics.Snapshot();
        Assert.Equal(1, stats.SkippedLateNotes);
        Assert.Equal(2, stats.Late);
        Assert.Equal(TimeSpan.FromMilliseconds(399), stats.MaxLateness);
    }

    [Fact]
    public void DisconnectedOutput_IsCountedNotThrown()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 10), Note(5, 10));
        f.Engine.Play(Tick.Zero);
        f.PumpAt(0);

        f.Providers[0].RemovePort(f.Port.Key);
        f.PumpAt(20);

        var stats = f.Engine.Statistics.Snapshot();
        Assert.Equal(2, stats.Dropped);
        Assert.Equal(1, stats.Dispatched);
    }

    [Fact]
    public void ActiveNoteTable_OverflowDropsNotesInsteadOfHanging()
    {
        using var f = new PlaybackFixture(new PlaybackOptions { MaxActiveNotes = 2 }, Immediate);
        f.Load(Note(0, 10, 60), Note(0, 10, 62), Note(0, 10, 64));
        f.Engine.Play(Tick.Zero);

        f.PumpAt(0);
        f.PumpAt(10);

        Assert.Equal(["903C64@0", "903E64@0", "803C40@10", "803E40@10"], f.Sent());
        Assert.Equal(1, f.Engine.Statistics.Snapshot().NoteOverflow);
    }

    [Fact]
    public void MixedOutputs_UseTheirOwnDeliveryStrategy()
    {
        using var f = new PlaybackFixture(null, EndpointCapabilities.ScheduledDelivery, Immediate);
        var a = new Track(TrackId.New(), "a", [Note(10, 5)]);
        var b = new Track(TrackId.New(), "b", [Note(10, 5)]);
        var sequence = Sequence.CreateEmpty(Resolution).WithTrack(a).WithTrack(b);
        f.Engine.Load(PlaybackPlanCompiler.Compile(sequence, new Dictionary<TrackId, PlanTrackBinding> { [a.Id] = new(0), [b.Id] = new(1) }));
        f.Engine.Play(Tick.Zero);

        f.PumpAt(0);
        Assert.Equal(["903C64@10", "803C40@15"], f.Sent(0));
        Assert.Empty(f.Sent(1));

        f.PumpAt(10);
        f.PumpAt(15);
        Assert.Equal(["903C64@10", "803C40@15"], f.Sent(1));
    }

    [Fact]
    public void SeekWhileStopped_SetsThePositionForPlay()
    {
        using var f = new PlaybackFixture(null, Immediate);
        f.Load(Note(0, 10), Note(300, 10));
        f.Engine.Seek(new Tick(300));
        Assert.Equal(new Tick(300), f.Engine.Position);

        f.Engine.Play();
        f.PumpAt(0);

        Assert.Equal(["903C64@0"], f.Sent());
    }

    [Fact]
    public void Pump_ReturnsInfiniteWhenIdle()
    {
        using var f = new PlaybackFixture();

        Assert.Equal(Timeout.InfiniteTimeSpan, f.PumpAt(0));
    }

    [Fact]
    public void Pump_WaitsForScheduledEventsMinusLookAhead()
    {
        using var f = new PlaybackFixture();
        f.Load(Note(100, 10));
        f.Engine.Play(Tick.Zero);

        Assert.Equal(TimeSpan.FromMilliseconds(80), f.PumpAt(0));
    }

    [Fact]
    public void Statistics_ReportPercentiles()
    {
        var stats = new TimingStatistics(TimeSpan.FromMilliseconds(2));
        for (var i = 0; i < 98; i++)
        {
            stats.RecordDispatch(TimeSpan.FromMilliseconds(0.1));
        }

        stats.RecordDispatch(TimeSpan.FromMilliseconds(3));
        stats.RecordDispatch(TimeSpan.FromMilliseconds(70));

        var snapshot = stats.Snapshot();
        Assert.Equal(TimeSpan.FromMilliseconds(0.25), snapshot.Percentile(50));
        Assert.Equal(TimeSpan.FromMilliseconds(5), snapshot.Percentile(99));
        Assert.Equal(TimeSpan.FromMilliseconds(70), snapshot.Percentile(100));
        Assert.Equal(2, snapshot.Late);
        Assert.Equal(TimeSpan.Zero, new TimingStatistics(TimeSpan.Zero).Snapshot().Percentile(50));
    }
}
