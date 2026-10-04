using Cadence.Domain.Time;

namespace Cadence.Application.Editing;

/// <summary>Note values used for snapping, quantizing, and new-note lengths.</summary>
public enum GridDivision
{
    Bar,
    Half,
    Quarter,
    Eighth,
    Sixteenth,
    ThirtySecond,
    QuarterTriplet,
    EighthTriplet,
    SixteenthTriplet,
}

/// <summary>
/// A musical grid laid over the meter map. Grid lines restart at every bar line, so a quarter-note
/// grid in 7/8 still lands on each bar's downbeat.
/// </summary>
public static class MusicalGrid
{
    public static readonly IReadOnlyList<GridDivision> All = Enum.GetValues<GridDivision>();

    /// <summary>The length of one grid step in ticks at <paramref name="position"/>. May be fractional for triplets at unusual resolutions.</summary>
    public static double StepTicks(GridDivision division, MeterMap meter, Tick position)
    {
        ArgumentNullException.ThrowIfNull(meter);
        double quarter = meter.Ppqn.TicksPerQuarterNote;
        return division switch
        {
            GridDivision.Bar => BarLength(meter, position),
            GridDivision.Half => quarter * 2,
            GridDivision.Quarter => quarter,
            GridDivision.Eighth => quarter / 2,
            GridDivision.Sixteenth => quarter / 4,
            GridDivision.ThirtySecond => quarter / 8,
            GridDivision.QuarterTriplet => quarter * 2 / 3,
            GridDivision.EighthTriplet => quarter / 3,
            GridDivision.SixteenthTriplet => quarter / 6,
            _ => throw new ArgumentOutOfRangeException(nameof(division), division, null),
        };
    }

    /// <summary>One grid step as a whole number of ticks (at least one), for note lengths and nudges.</summary>
    public static long StepLength(GridDivision division, MeterMap meter, Tick position) =>
        Math.Max(1, (long)Math.Round(StepTicks(division, meter, position)));

    /// <summary>The nearest grid line to <paramref name="tick"/>.</summary>
    public static long Snap(long tick, GridDivision division, MeterMap meter) => Line(tick, division, meter, Math.Round);

    /// <summary>The grid line at or before <paramref name="tick"/>.</summary>
    public static long SnapDown(long tick, GridDivision division, MeterMap meter) => Line(tick, division, meter, Math.Floor);

    /// <summary>
    /// The grid line nearest <paramref name="tick"/> together with its index within the bar, so swing
    /// can delay every second line.
    /// </summary>
    internal static (long Tick, long Index, double Step) Nearest(long tick, GridDivision division, MeterMap meter)
    {
        var position = new Tick(Math.Max(0, tick));
        var bar = meter.BarStart(position).Value;
        var step = StepTicks(division, meter, position);
        var index = (long)Math.Round((position.Value - bar) / step);
        return (bar + (long)Math.Round(index * step), index, step);
    }

    private static long Line(long tick, GridDivision division, MeterMap meter, Func<double, double> round)
    {
        ArgumentNullException.ThrowIfNull(meter);
        var position = new Tick(Math.Max(0, tick));
        var bar = meter.BarStart(position).Value;
        var step = StepTicks(division, meter, position);
        return bar + (long)Math.Round(round((position.Value - bar) / step) * step);
    }

    private static double BarLength(MeterMap meter, Tick position)
    {
        var signature = meter.SignatureAt(position);
        return signature.TryGetTicksPerBeat(meter.Ppqn, out var beat) ? beat.Value * (double)signature.Numerator : meter.Ppqn.TicksPerQuarterNote * 4.0;
    }
}
