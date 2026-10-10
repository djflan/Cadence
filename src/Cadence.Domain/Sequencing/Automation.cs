using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Time;

namespace Cadence.Domain.Sequencing;

/// <summary>Stable identity of an automation lane within a project.</summary>
public readonly record struct AutomationLaneId(Guid Value)
{
    public static AutomationLaneId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum AutomationParameter
{
    Controller,
    PitchBend,
    ChannelPressure,
}

/// <summary>
/// What an automation lane controls: a controller, pitch bend, or channel pressure on one channel
/// (ADR 0020). Controllers whose lone values mean something other than a level are refused: bank
/// select, the LSB controllers (32-63), data entry and increment/decrement, RPN/NRPN selectors, and
/// channel mode messages.
/// </summary>
public readonly record struct AutomationTarget
{
    private AutomationTarget(AutomationParameter parameter, MidiChannel channel, ControllerNumber controller)
    {
        Parameter = parameter;
        Channel = channel;
        Controller = controller;
    }

    public AutomationParameter Parameter { get; }

    public MidiChannel Channel { get; }

    /// <summary>The controller, for <see cref="AutomationParameter.Controller"/> targets.</summary>
    public ControllerNumber Controller { get; }

    /// <summary>The bits MIDI 1.0 sends for this target: 14 for pitch bend, 7 otherwise.</summary>
    public int Midi1Bits => Parameter == AutomationParameter.PitchBend ? 14 : 7;

    /// <summary>The value at rest: pitch bend centred, anything else at its minimum.</summary>
    public ControlValue Rest => Parameter == AutomationParameter.PitchBend ? ControlValue.Center : ControlValue.Min;

    /// <exception cref="ArgumentException">The controller cannot be automated (see <see cref="IsAutomatable"/>).</exception>
    public static AutomationTarget ForController(MidiChannel channel, ControllerNumber controller) =>
        IsAutomatable(controller)
            ? new(AutomationParameter.Controller, channel, controller)
            : throw new ArgumentException($"Controller {controller} cannot be automated.", nameof(controller));

    public static AutomationTarget ForPitchBend(MidiChannel channel) => new(AutomationParameter.PitchBend, channel, default);

    public static AutomationTarget ForChannelPressure(MidiChannel channel) => new(AutomationParameter.ChannelPressure, channel, default);

    public static bool IsAutomatable(ControllerNumber controller) =>
        !(controller.IsBankSelect || controller.Value is >= 32 and <= 63 or 6 or 96 or 97 || controller.IsParameterNumberSelector || controller.IsChannelMode);

    /// <summary>The target <paramref name="e"/> sets, or null if no lane could control it.</summary>
    public static AutomationTarget? Of(ChannelEvent e) => e switch
    {
        ControllerEvent c when IsAutomatable(c.Controller) => new(AutomationParameter.Controller, c.Channel, c.Controller),
        PitchBendEvent p => ForPitchBend(p.Channel),
        ChannelPressureEvent p => ForChannelPressure(p.Channel),
        _ => null,
    };

    public AutomationTarget WithChannel(MidiChannel channel) => new(Parameter, channel, Controller);

    /// <summary><paramref name="value"/> as MIDI 1.0 sends it for this target, scaled back up.</summary>
    public ControlValue AtMidi1Resolution(ControlValue value) =>
        Midi1Bits == 14 ? ControlValue.FromFourteenBit(value.ToFourteenBit()) : ControlValue.FromSevenBit(value.ToSevenBit());

    /// <summary>An event setting this target to <paramref name="value"/>. It is rendered, not stored, so it has no ID.</summary>
    public ChannelEvent CreateEvent(Tick position, ControlValue value) => Parameter switch
    {
        AutomationParameter.PitchBend => new PitchBendEvent(default, position, Channel, value),
        AutomationParameter.ChannelPressure => new ChannelPressureEvent(default, position, Channel, value),
        _ => new ControllerEvent(default, position, Channel, Controller, value),
    };
}

/// <summary>How a lane moves from one point to the next.</summary>
public enum AutomationCurve
{
    /// <summary>Stays at the point's value until the next point.</summary>
    Hold,

    /// <summary>Moves in a straight line to the next point's value.</summary>
    Linear,
}

/// <summary>A breakpoint: the value at <paramref name="Position"/>, and how the lane leaves it.</summary>
public readonly record struct AutomationPoint(Tick Position, ControlValue Value, AutomationCurve Curve = AutomationCurve.Linear);

/// <summary>
/// An immutable automation lane: a target and breakpoints in arrangement time. Before its first point
/// the lane holds the first value; after its last, the last value.
/// </summary>
public sealed class AutomationLane
{
    public const int MaxPoints = 1_000_000;

    private AutomationLane(AutomationLaneId id, AutomationTarget target, ImmutableArray<AutomationPoint> points)
    {
        Id = id;
        Target = target;
        Points = points;
    }

    /// <exception cref="ArgumentException">Two points share a position, or there are too many.</exception>
    public AutomationLane(AutomationLaneId id, AutomationTarget target, IEnumerable<AutomationPoint> points)
        : this(id, target, Sort(points))
    {
    }

    public static AutomationLane Create(AutomationTarget target) => new(AutomationLaneId.New(), target, ImmutableArray<AutomationPoint>.Empty);

    public AutomationLaneId Id { get; }

    public AutomationTarget Target { get; }

    /// <summary>Breakpoints in position order, at most one per tick.</summary>
    public ImmutableArray<AutomationPoint> Points { get; }

    /// <summary>The position of the last point, or zero if there are none.</summary>
    public Tick EndPosition => Points.IsEmpty ? Tick.Zero : Points[^1].Position;

    /// <summary>The lane's value at <paramref name="position"/>, or null if it has no points.</summary>
    public ControlValue? ValueAt(Tick position)
    {
        if (Points.IsEmpty)
        {
            return null;
        }

        var index = LastAtOrBefore(position);
        if (index < 0)
        {
            return Points[0].Value;
        }

        var point = Points[index];
        return index + 1 < Points.Length && point.Curve == AutomationCurve.Linear
            ? Interpolate(point, Points[index + 1], position.Value)
            : point.Value;
    }

    /// <summary>Adds <paramref name="point"/>, replacing any point at the same position.</summary>
    public AutomationLane WithPoint(AutomationPoint point) =>
        new(Id, Target, Sort(Points.Where(p => p.Position != point.Position).Append(point)));

    /// <summary>Removes the points in [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public AutomationLane WithoutPoints(Tick from, Tick to)
    {
        var kept = Points.Where(p => p.Position < from || p.Position > to).ToImmutableArray();
        return kept.Length == Points.Length ? this : new AutomationLane(Id, Target, kept);
    }

    /// <summary>Replaces the points in [<paramref name="from"/>, <paramref name="to"/>] with <paramref name="points"/>.</summary>
    /// <exception cref="ArgumentException">Two of the new points share a position.</exception>
    public AutomationLane ReplaceRange(Tick from, Tick to, IEnumerable<AutomationPoint> points) =>
        new(Id, Target, Sort(Points.Where(p => p.Position < from || p.Position > to).Concat(points)));

    public AutomationLane WithPoints(IEnumerable<AutomationPoint> points) => new(Id, Target, Sort(points));

    public AutomationLane WithTarget(AutomationTarget target) => new(Id, target, Points);

    internal static ControlValue Interpolate(AutomationPoint from, AutomationPoint to, long position)
    {
        var span = to.Position.Value - from.Position.Value;
        var offset = Math.Clamp(position - from.Position.Value, 0, span);
        var value = (Int128)from.Value.Value + (((Int128)to.Value.Value - from.Value.Value) * offset / span);
        return new ControlValue((uint)value);
    }

    private int LastAtOrBefore(Tick position)
    {
        int low = 0, high = Points.Length - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Points[mid].Position <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    private static ImmutableArray<AutomationPoint> Sort(IEnumerable<AutomationPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = points.OrderBy(p => p.Position).ToImmutableArray();
        if (sorted.Length > MaxPoints)
        {
            throw new ArgumentException($"A lane holds at most {MaxPoints} points.", nameof(points));
        }

        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Position == sorted[i - 1].Position)
            {
                throw new ArgumentException($"Two points are at tick {sorted[i].Position}; use a hold curve for a jump.", nameof(points));
            }
        }

        return sorted;
    }
}

/// <summary>Turns automation lanes into the values MIDI 1.0 sends.</summary>
public static class AutomationRenderer
{
    /// <summary>How often linear segments are sampled: every 1/32 of a quarter note, at least every tick.</summary>
    public static TickSpan DefaultInterval(Ppqn ppqn) => new(Math.Max(1, ppqn.TicksPerQuarterNote / 32));

    /// <summary>
    /// The values to send for <paramref name="lane"/>, at its target's MIDI 1.0 resolution: the first value
    /// at tick 0, each point's value at its position, and linear segments every <paramref name="interval"/>.
    /// A value is sent only when it differs from the last one sent.
    /// </summary>
    public static IEnumerable<(Tick Position, ControlValue Value)> Sample(AutomationLane lane, TickSpan interval)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval.Value, 1, nameof(interval));
        var points = lane.Points;
        if (points.IsEmpty)
        {
            yield break;
        }

        ControlValue? last = null;
        var target = lane.Target;
        if (points[0].Position > Tick.Zero && Emit(points[0].Value, out var first))
        {
            yield return (Tick.Zero, first);
        }

        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (Emit(point.Value, out var value))
            {
                yield return (point.Position, value);
            }

            if (i + 1 == points.Length || point.Curve != AutomationCurve.Linear)
            {
                continue;
            }

            var next = points[i + 1];
            for (var tick = point.Position.Value + interval.Value; tick < next.Position.Value; tick += interval.Value)
            {
                if (Emit(AutomationLane.Interpolate(point, next, tick), out value))
                {
                    yield return (new Tick(tick), value);
                }
            }
        }

        bool Emit(ControlValue raw, out ControlValue sent)
        {
            sent = target.AtMidi1Resolution(raw);
            if (sent == last)
            {
                return false;
            }

            last = sent;
            return true;
        }
    }
}
