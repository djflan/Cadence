using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using CsCheck;

namespace Cadence.Tests.Unit.Domain.Sequencing;

public sealed class AutomationTests
{
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);
    private static readonly MidiChannel Two = MidiChannel.FromNumber(2);
    private static readonly AutomationTarget Volume = AutomationTarget.ForController(One, ControllerNumber.ChannelVolume);
    private static readonly Ppqn Resolution = new(480);

    private static AutomationPoint Point(long at, int value, AutomationCurve curve = AutomationCurve.Linear) =>
        new(new Tick(at), ControlValue.FromSevenBit(value), curve);

    private static AutomationLane Lane(AutomationTarget target, params AutomationPoint[] points) => new(AutomationLaneId.New(), target, points);

    private static List<(long, int)> Sevens(IEnumerable<(Tick Position, ControlValue Value)> samples) =>
        [.. samples.Select(s => (s.Position.Value, s.Value.ToSevenBit()))];

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(38)]
    [InlineData(63)]
    [InlineData(6)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(98)]
    [InlineData(101)]
    [InlineData(120)]
    [InlineData(127)]
    public void Targets_RefuseControllersWithoutALevel(int controller) =>
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForController(One, new ControllerNumber(controller)));

    [Fact]
    public void Of_FindsTheTargetAnEventSets()
    {
        Assert.Equal(Volume, AutomationTarget.Of(new ControllerEvent(Tick.Zero, One, ControllerNumber.ChannelVolume, ControlValue.Max)));
        Assert.Equal(AutomationTarget.ForPitchBend(Two), AutomationTarget.Of(new PitchBendEvent(Tick.Zero, Two, ControlValue.Center)));
        Assert.Equal(AutomationTarget.ForChannelPressure(One), AutomationTarget.Of(new ChannelPressureEvent(Tick.Zero, One, ControlValue.Max)));
        Assert.Null(AutomationTarget.Of(new ControllerEvent(Tick.Zero, One, ControllerNumber.BankSelectMsb, ControlValue.Min)));
        Assert.Null(AutomationTarget.Of(new NoteEvent(Tick.Zero, new TickSpan(1), One, NoteNumber.MiddleC, Velocity.Max)));
        Assert.Equal(AutomationTarget.ForController(Two, ControllerNumber.ChannelVolume), Volume.WithChannel(Two));
    }

    [Fact]
    public void ValueAt_HoldsBeforeAndAfterAndFollowsCurves()
    {
        var lane = Lane(Volume, Point(100, 0), Point(200, 100, AutomationCurve.Hold), Point(300, 20));

        Assert.Null(Lane(Volume).ValueAt(Tick.Zero));
        Assert.Equal(0, lane.ValueAt(Tick.Zero)!.Value.ToSevenBit());
        Assert.Equal(50, lane.ValueAt(new Tick(150))!.Value.ToSevenBit());
        Assert.Equal(100, lane.ValueAt(new Tick(299))!.Value.ToSevenBit());
        Assert.Equal(20, lane.ValueAt(new Tick(300))!.Value.ToSevenBit());
        Assert.Equal(20, lane.ValueAt(new Tick(9999))!.Value.ToSevenBit());
        Assert.Equal(new Tick(300), lane.EndPosition);
    }

    [Fact]
    public void Points_AreSortedAndUniquePerTick()
    {
        var lane = Lane(Volume, Point(300, 1), Point(100, 2));

        Assert.Equal([100L, 300L], lane.Points.Select(p => p.Position.Value));
        Assert.Throws<ArgumentException>(() => Lane(Volume, Point(100, 1), Point(100, 2)));
        Assert.Equal([2, 9], lane.WithPoint(Point(300, 9)).Points.Select(p => p.Value.ToSevenBit()));
        Assert.Equal([300L], lane.WithoutPoints(new Tick(0), new Tick(200)).Points.Select(p => p.Position.Value));
        Assert.Same(lane, lane.WithoutPoints(new Tick(500), new Tick(600)));
        Assert.Equal([50L, 60L, 300L], lane.ReplaceRange(new Tick(0), new Tick(200), [Point(50, 1), Point(60, 1)]).Points.Select(p => p.Position.Value));
    }

    [Fact]
    public void Sample_StartsAtZeroAndSendsChangesOnly()
    {
        var lane = Lane(Volume, Point(960, 10, AutomationCurve.Hold), Point(1920, 10, AutomationCurve.Hold), Point(2000, 20, AutomationCurve.Hold));

        Assert.Equal([(0L, 10), (2000L, 20)], Sevens(AutomationRenderer.Sample(lane, AutomationRenderer.DefaultInterval(Resolution))));
        Assert.Empty(AutomationRenderer.Sample(Lane(Volume), new TickSpan(1)));
    }

    [Fact]
    public void Sample_RampsLinearSegmentsOnTheInterval()
    {
        var lane = Lane(Volume, Point(0, 0), Point(60, 3));

        Assert.Equal([(0L, 0), (20L, 1), (40L, 2), (60L, 3)], Sevens(AutomationRenderer.Sample(lane, new TickSpan(10))));
        Assert.Equal(15, AutomationRenderer.DefaultInterval(Resolution).Value);
        Assert.Equal(1, AutomationRenderer.DefaultInterval(new Ppqn(24)).Value);
    }

    [Fact]
    public void Sample_PitchBendUsesFourteenBits()
    {
        var bend = Lane(AutomationTarget.ForPitchBend(One), new AutomationPoint(Tick.Zero, ControlValue.FromFourteenBit(8192)), new AutomationPoint(new Tick(100), ControlValue.FromFourteenBit(8292)));

        var samples = AutomationRenderer.Sample(bend, new TickSpan(1)).ToList();

        Assert.Equal(101, samples.Count);
        Assert.Equal(8292, samples[^1].Value.ToFourteenBit());
    }

    [Fact]
    public void Sample_HandlesHugeSpansWithoutOverflow()
    {
        var lane = Lane(Volume, new AutomationPoint(Tick.Zero, ControlValue.Min), new AutomationPoint(new Tick(long.MaxValue / 2), ControlValue.Max));

        Assert.InRange(lane.ValueAt(new Tick(long.MaxValue / 4))!.Value.Value, ControlValue.Center.Value - 2, ControlValue.Center.Value + 1);
    }

    private static readonly Gen<AutomationLane> GenLane =
        Gen.Select(Gen.Long[0, 5000], Gen.Int[0, 127], Gen.Bool).Array[1, 12].Select(points =>
            Lane(Volume, [.. points.DistinctBy(p => p.Item1).Select(p => Point(p.Item1, p.Item2, p.Item3 ? AutomationCurve.Linear : AutomationCurve.Hold))]));

    [Fact]
    public void Sample_Properties() =>
        Gen.Select(GenLane, Gen.Long[1, 200]).Sample((lane, interval) =>
        {
            var samples = AutomationRenderer.Sample(lane, new TickSpan(interval)).ToList();
            var positions = samples.Select(s => s.Position.Value).ToList();
            var values = samples.Select(s => s.Value.ToSevenBit()).ToList();
            return samples[0].Position == Tick.Zero
                && positions.Zip(positions.Skip(1)).All(p => p.First < p.Second)
                && values.Zip(values.Skip(1)).All(v => v.First != v.Second)
                && lane.Points.All(p => p.Value.ToSevenBit() == values[positions.FindLastIndex(t => t <= p.Position.Value)])
                && samples.All(s => s.Value == lane.Target.AtMidi1Resolution(lane.ValueAt(s.Position)!.Value));
        });

    private static Track TrackWith(IEnumerable<TrackEvent> events, params AutomationLane[] lanes) =>
        new(TrackId.New(), "t", Track.FromEvents(TrackId.New(), "t", events).Clips, automation: lanes);

    [Fact]
    public void Render_LetsAutomationReplaceClipEventsOnItsTarget()
    {
        var note = new NoteEvent(Tick.Zero, new TickSpan(10), One, NoteNumber.MiddleC, Velocity.Max);
        var volume = new ControllerEvent(new Tick(5), One, ControllerNumber.ChannelVolume, ControlValue.Max);
        var otherChannel = new ControllerEvent(new Tick(5), Two, ControllerNumber.ChannelVolume, ControlValue.Max);
        var track = TrackWith([note, volume, otherChannel], Lane(Volume, Point(0, 64, AutomationCurve.Hold)));

        var rendered = TrackRendering.Render(track, Resolution);

        Assert.Equal(1, rendered.SuppressedEvents);
        Assert.DoesNotContain(rendered.Events, e => e.Id == volume.Id);
        Assert.Contains(rendered.Events, e => e.Id == otherChannel.Id);
        var automation = Assert.Single(rendered.Events.OfType<ControllerEvent>(), e => e.Channel == One);
        Assert.Equal(64, automation.Value.ToSevenBit());
    }

    [Fact]
    public void Render_ComparesTargetsOnTheOverrideChannel()
    {
        var onTwo = new ControllerEvent(new Tick(5), Two, ControllerNumber.ChannelVolume, ControlValue.Max);
        var track = TrackWith([onTwo], Lane(Volume, Point(0, 64)), Lane(Volume.WithChannel(Two), Point(0, 1)));

        var routed = TrackRendering.Render(track, Resolution, channelOverride: Two);
        Assert.Equal((1, 1), (routed.SuppressedEvents, routed.DroppedLanes));
        Assert.Equal(64, Assert.Single(routed.Events.OfType<ControllerEvent>()).Value.ToSevenBit());

        var exported = TrackRendering.Render(track, Resolution);
        Assert.Equal((1, 0), (exported.SuppressedEvents, exported.DroppedLanes));
        Assert.Equal(2, exported.Events.Length);
    }

    [Fact]
    public void Render_PutsAutomationAfterClipEventsAtTheSameTickAndPhase()
    {
        var expression = new ControllerEvent(Tick.Zero, One, ControllerNumber.Expression, ControlValue.Max);
        var track = TrackWith([expression], Lane(Volume, Point(0, 64)));

        var rendered = TrackRendering.Render(track, Resolution);

        Assert.Equal(expression.Id, rendered.Events[0].Id);
        Assert.Equal(ControllerNumber.ChannelVolume, ((ControllerEvent)rendered.Events[1]).Controller);
    }

    [Fact]
    public void Render_WithoutLanes_IsTheArrangedEvents()
    {
        var track = TrackWith([new ControllerEvent(Tick.Zero, One, ControllerNumber.Expression, ControlValue.Max)], Lane(Volume));

        Assert.Equal(track.ArrangedEvents, TrackRendering.Render(track, Resolution).Events);
    }

    [Fact]
    public void Track_LanesHaveUniqueTargetsAndCountTowardsTheEnd()
    {
        var lane = Lane(Volume, Point(5000, 1));
        var track = Track.Create("t").WithLane(lane);

        Assert.Equal(new Tick(5000), track.EndPosition);
        Assert.Throws<ArgumentException>(() => track.WithLane(Lane(Volume)));
        Assert.Same(lane, track.FindLane(lane.Id));
        Assert.Equal(2, track.WithLane(lane.WithPoint(Point(10, 2))).Automation.Single().Points.Length);
        Assert.Empty(track.RemoveLane(lane.Id).Automation);
        Assert.Single(track.WithName("u").WithMuted(true).WithClips([]).ClearRange(Tick.Zero, new Tick(10)).Automation);
    }
}
