using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cadence.Desktop.Theme;
using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Desktop.Controls;

/// <summary>
/// The arrangement view: a bar ruler, one lane per track, and every note as a block. Drawn directly
/// with the GPU-backed renderer and culled to the visible region, so dense projects stay smooth.
/// Clicking or dragging on the ruler or lanes requests a seek.
/// </summary>
public sealed class TimelineView : Control
{
    public static readonly StyledProperty<Project?> ProjectProperty = AvaloniaProperty.Register<TimelineView, Project?>(nameof(Project));
    public static readonly StyledProperty<double> PixelsPerQuarterProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(PixelsPerQuarter), 40);
    public static readonly StyledProperty<double> LaneHeightProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(LaneHeight), 64);
    public static readonly StyledProperty<int> SelectedIndexProperty = AvaloniaProperty.Register<TimelineView, int>(nameof(SelectedIndex), -1);
    public static readonly StyledProperty<double> VisibleLeftProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleLeft));
    public static readonly StyledProperty<double> VisibleWidthProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleWidth), double.PositiveInfinity);

    public const double RulerHeight = 28;

    private static readonly IBrush LaneAlt = new SolidColorBrush(Palette.LaneAlt);
    private static readonly IBrush LaneSelected = new SolidColorBrush(Palette.LaneSelected);
    private static readonly IBrush RulerText = new SolidColorBrush(Palette.TextSecondary);
    private static readonly IBrush LoopFill = new SolidColorBrush(Palette.Loop, 0.08);
    private static readonly IBrush LoopBar = new SolidColorBrush(Palette.Loop, 0.85);
    private static readonly IPen BarPen = new Pen(new SolidColorBrush(Palette.GridBar), 1);
    private static readonly IPen BeatPen = new Pen(new SolidColorBrush(Palette.GridBeat), 1);
    private static readonly Typeface RulerFace = new("Inter");

    static TimelineView()
    {
        AffectsRender<TimelineView>(ProjectProperty, PixelsPerQuarterProperty, LaneHeightProperty, SelectedIndexProperty, VisibleLeftProperty, VisibleWidthProperty);
        AffectsMeasure<TimelineView>(ProjectProperty, PixelsPerQuarterProperty, LaneHeightProperty);
        FocusableProperty.OverrideDefaultValue<TimelineView>(true);
    }

    public Project? Project
    {
        get => GetValue(ProjectProperty);
        set => SetValue(ProjectProperty, value);
    }

    public double PixelsPerQuarter
    {
        get => GetValue(PixelsPerQuarterProperty);
        set => SetValue(PixelsPerQuarterProperty, value);
    }

    public double LaneHeight
    {
        get => GetValue(LaneHeightProperty);
        set => SetValue(LaneHeightProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>The horizontal scroll offset, used to draw only what is visible.</summary>
    public double VisibleLeft
    {
        get => GetValue(VisibleLeftProperty);
        set => SetValue(VisibleLeftProperty, value);
    }

    public double VisibleWidth
    {
        get => GetValue(VisibleWidthProperty);
        set => SetValue(VisibleWidthProperty, value);
    }

    /// <summary>Raised with the tick under the pointer when the user clicks or drags to seek.</summary>
    public event EventHandler<long>? SeekRequested;

    public double TickToX(long tick) => Project is { } p ? tick * PixelsPerQuarter / p.Sequence.Ppqn.TicksPerQuarterNote : 0;

    public long XToTick(double x) => Project is { } p ? (long)Math.Max(0, x * p.Sequence.Ppqn.TicksPerQuarterNote / PixelsPerQuarter) : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Project is not { } project)
        {
            return new Size(0, RulerHeight);
        }

        var sequence = project.Sequence;
        var endTick = sequence.EndPosition.Value + (16L * sequence.Ppqn.TicksPerQuarterNote);
        var width = Math.Max(TickToX(endTick), double.IsFinite(availableSize.Width) ? availableSize.Width : 0);
        return new Size(width, RulerHeight + (Math.Max(1, sequence.Tracks.Length) * LaneHeight));
    }

    public override void Render(DrawingContext context)
    {
        if (Project is not { } project)
        {
            return;
        }

        var sequence = project.Sequence;
        var left = Math.Max(0, VisibleLeft - 50);
        var right = Math.Min(Bounds.Width, VisibleLeft + (double.IsFinite(VisibleWidth) ? VisibleWidth : Bounds.Width) + 50);
        var firstTick = XToTick(left);
        var lastTick = XToTick(right);

        for (var lane = 0; lane < Math.Max(1, sequence.Tracks.Length); lane++)
        {
            var top = RulerHeight + (lane * LaneHeight);
            var brush = lane == SelectedIndex ? LaneSelected : lane % 2 == 1 ? LaneAlt : null;
            if (brush is not null)
            {
                context.FillRectangle(brush, new Rect(left, top, right - left, LaneHeight));
            }
        }

        if (project.Loop is { } loop)
        {
            var x0 = TickToX(loop.Start.Value);
            var x1 = TickToX(loop.End.Value);
            context.FillRectangle(LoopFill, new Rect(x0, 0, x1 - x0, Bounds.Height));
            context.FillRectangle(LoopBar, new Rect(x0, 2, x1 - x0, 4), 2);
        }

        DrawGrid(context, sequence, firstTick, lastTick);

        for (var lane = 0; lane < sequence.Tracks.Length; lane++)
        {
            DrawNotes(context, sequence.Tracks[lane], lane, firstTick, lastTick);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(this);
            SeekRequested?.Invoke(this, XToTick(e.GetPosition(this).X));
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Pointer.Captured, this))
        {
            SeekRequested?.Invoke(this, XToTick(e.GetPosition(this).X));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    private void DrawGrid(DrawingContext context, Sequence sequence, long firstTick, long lastTick)
    {
        var meter = sequence.MeterMap;
        var position = meter.BarStart(new Tick(firstTick));
        var guard = 0;
        while (position.Value <= lastTick && guard++ < 10_000)
        {
            var bar = meter.ToBarBeatTick(position).Bar;
            var signature = meter.SignatureAt(position);
            signature.TryGetTicksPerBeat(sequence.Ppqn, out var beat);
            var x = Math.Round(TickToX(position.Value)) + 0.5;
            context.DrawLine(BarPen, new Point(x, RulerHeight - 8), new Point(x, Bounds.Height));

            if (PixelsPerQuarter >= 14)
            {
                for (var b = 1; b < signature.Numerator; b++)
                {
                    var bx = Math.Round(TickToX(position.Value + (b * beat.Value))) + 0.5;
                    context.DrawLine(BeatPen, new Point(bx, RulerHeight), new Point(bx, Bounds.Height));
                }
            }

            var labelEvery = PixelsPerQuarter < 10 ? 4 : 1;
            if ((bar - 1) % labelEvery == 0)
            {
                var text = new FormattedText(bar.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, RulerFace, 11, RulerText);
                context.DrawText(text, new Point(x + 4, 9));
            }

            if (!meter.TryGetTick(new BarBeatTick(bar + 1, 1, 0), out var next) || next <= position)
            {
                break;
            }

            position = next;
        }
    }

    private void DrawNotes(DrawingContext context, Track track, int lane, long firstTick, long lastTick)
    {
        var color = Palette.Track(lane);
        var top = RulerHeight + (lane * LaneHeight) + 6;
        var height = LaneHeight - 12;
        var notes = track.Events.OfType<NoteEvent>().ToList();
        if (notes.Count == 0)
        {
            return;
        }

        var low = notes.Min(n => n.Note.Value);
        var high = Math.Max(low + 12, notes.Max(n => n.Note.Value));
        var span = high - low + 1;
        var noteHeight = Math.Clamp(height / span, 2, 6);
        var faded = track.IsMuted ? 0.35 : 1.0;

        foreach (var note in notes)
        {
            if (note.EndPosition.Value < firstTick || note.Position.Value > lastTick)
            {
                continue;
            }

            var x = TickToX(note.Position.Value);
            var w = Math.Max(2, TickToX(note.EndPosition.Value) - x - 1);
            var y = top + ((high - note.Note.Value) * (height - noteHeight) / Math.Max(1, span - 1));
            var opacity = (0.45 + (0.55 * note.Velocity.Value / 127.0)) * faded;
            context.FillRectangle(new SolidColorBrush(color, opacity), new Rect(x, y, w, noteHeight), 1.5f);
        }
    }
}
