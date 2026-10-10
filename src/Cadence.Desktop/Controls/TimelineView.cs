using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cadence.Application.Recording;
using Cadence.Desktop.Theme;
using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Desktop.Controls;

/// <summary>
/// The arrangement: one lane per track, with the track's material drawn as a region (a coloured
/// block from its first to its last bar, named on a header strip) containing every note. Drawn
/// directly with the GPU-backed renderer and culled to the visible region, so dense projects stay
/// smooth. Click a lane to select its track; double-click to open it in the piano roll.
/// </summary>
public sealed class TimelineView : Control
{
    public static readonly StyledProperty<Project?> ProjectProperty = AvaloniaProperty.Register<TimelineView, Project?>(nameof(Project));
    public static readonly StyledProperty<double> PixelsPerQuarterProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(PixelsPerQuarter), 40);
    public static readonly StyledProperty<double> LaneHeightProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(LaneHeight), 50);
    public static readonly StyledProperty<IReadOnlySet<int>> SelectedLanesProperty = AvaloniaProperty.Register<TimelineView, IReadOnlySet<int>>(nameof(SelectedLanes), new HashSet<int>());
    public static readonly StyledProperty<double> VisibleLeftProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleLeft));
    public static readonly StyledProperty<double> VisibleWidthProperty = AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleWidth), double.PositiveInfinity);
    public static readonly StyledProperty<int> RecordingLaneProperty = AvaloniaProperty.Register<TimelineView, int>(nameof(RecordingLane), -1);
    public static readonly StyledProperty<IReadOnlyList<RecordingNote>> RecordingPreviewProperty = AvaloniaProperty.Register<TimelineView, IReadOnlyList<RecordingNote>>(nameof(RecordingPreview), []);

    private static readonly IBrush LaneBrush = new SolidColorBrush(Palette.Lane);
    private static readonly IBrush LaneAltBrush = new SolidColorBrush(Palette.LaneAlt);
    private static readonly IBrush LaneSelected = new SolidColorBrush(Palette.LaneSelected);
    private static readonly IPen LaneDivider = new Pen(new SolidColorBrush(Palette.LaneDivider), 1);
    private static readonly IBrush RecordFill = new SolidColorBrush(Palette.Record, 0.28);
    private static readonly IPen RecordEdge = new Pen(new SolidColorBrush(Palette.Record, 0.9), 1);
    private static readonly IBrush RecordNote = new SolidColorBrush(Palette.Lighten(Palette.Record, 0.35));
    private static readonly IBrush RegionText = new SolidColorBrush(Color.Parse("#141416"));
    private static readonly Typeface RegionFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    // Region drag state
    private int _dragLane = -1;
    private Point _dragPress;
    private long _dragDelta;
    private bool _dragCopy;
    private bool _dragging;

    static TimelineView()
    {
        AffectsRender<TimelineView>(ProjectProperty, PixelsPerQuarterProperty, LaneHeightProperty, SelectedLanesProperty, VisibleLeftProperty, VisibleWidthProperty, RecordingLaneProperty, RecordingPreviewProperty);
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

    /// <summary>Indices of the lanes to highlight as selected.</summary>
    public IReadOnlySet<int> SelectedLanes
    {
        get => GetValue(SelectedLanesProperty);
        set => SetValue(SelectedLanesProperty, value);
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

    /// <summary>The lane being recorded into, or -1.</summary>
    public int RecordingLane
    {
        get => GetValue(RecordingLaneProperty);
        set => SetValue(RecordingLaneProperty, value);
    }

    /// <summary>Notes of the take being recorded, drawn live in red.</summary>
    public IReadOnlyList<RecordingNote> RecordingPreview
    {
        get => GetValue(RecordingPreviewProperty);
        set => SetValue(RecordingPreviewProperty, value);
    }

    /// <summary>Raised with the lane index and modifiers when a lane is clicked.</summary>
    public event EventHandler<(int Lane, KeyModifiers Modifiers)>? LaneClicked;

    /// <summary>Raised with the lane index when a lane is double-clicked.</summary>
    public event EventHandler<int>? LaneDoubleClicked;

    /// <summary>Raised when a region is dragged sideways: the lane, the snapped shift in ticks, and whether to copy.</summary>
    public event EventHandler<(int Lane, long DeltaTicks, bool Copy)>? RegionDragged;

    public double TickToX(long tick) => Project is { } p ? TimeGrid.TickToX(tick, p.Sequence, PixelsPerQuarter) : 0;

    public long XToTick(double x) => Project is { } p ? TimeGrid.XToTick(x, p.Sequence, PixelsPerQuarter) : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Project is not { } project)
        {
            return default;
        }

        var sequence = project.Sequence;
        var endTick = sequence.EndPosition.Value + (32L * sequence.Ppqn.TicksPerQuarterNote);
        var width = Math.Max(TickToX(endTick), double.IsFinite(availableSize.Width) ? availableSize.Width : 0);
        var height = Math.Max(sequence.Tracks.Length * LaneHeight, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
        return new Size(width, height);
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
        var lanes = sequence.Tracks.Length;

        context.FillRectangle(LaneBrush, new Rect(left, 0, right - left, Bounds.Height));
        for (var lane = 0; lane < lanes; lane++)
        {
            var top = lane * LaneHeight;
            var brush = SelectedLanes.Contains(lane) ? LaneSelected : lane % 2 == 1 ? LaneAltBrush : null;
            if (brush is not null)
            {
                context.FillRectangle(brush, new Rect(left, top, right - left, LaneHeight));
            }
        }

        TimeGrid.DrawLines(context, sequence, PixelsPerQuarter, 0, new Rect(left, 0, right - left, Bounds.Height));
        TimeGrid.ShadeCycle(context, sequence, project.Loop, PixelsPerQuarter, 0, new Rect(left, 0, right - left, Bounds.Height));

        for (var lane = 0; lane < lanes; lane++)
        {
            var bottom = Math.Round((lane + 1) * LaneHeight) - 0.5;
            context.DrawLine(LaneDivider, new Point(left, bottom), new Point(right, bottom));
            DrawRegion(context, sequence.Tracks[lane], lane, left, right);
        }

        if (RecordingLane >= 0 && RecordingLane < lanes && RecordingPreview.Count > 0)
        {
            DrawTake(context, RecordingLane);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (Project is not { } project || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var lane = (int)(point.Position.Y / LaneHeight);
        if (lane >= 0 && lane < project.Sequence.Tracks.Length)
        {
            Focus();
            if (e.ClickCount >= 2)
            {
                LaneDoubleClicked?.Invoke(this, lane);
            }
            else
            {
                LaneClicked?.Invoke(this, (lane, e.KeyModifiers));
                if (RegionExtent(project.Sequence.Tracks[lane]) is var (start, end)
                    && point.Position.X >= TickToX(start) && point.Position.X <= TickToX(end))
                {
                    _dragLane = lane;
                    _dragPress = point.Position;
                    _dragDelta = 0;
                    _dragCopy = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
                    _dragging = false;
                    e.Pointer.Capture(this);
                }
            }

            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragLane < 0 || Project is not { } project || !ReferenceEquals(e.Pointer.Captured, this))
        {
            return;
        }

        if (_dragLane >= project.Sequence.Tracks.Length)
        {
            // The track went away mid-drag (for example, undo).
            _dragLane = -1;
            InvalidateVisual();
            return;
        }

        var x = e.GetPosition(this).X;
        _dragging |= Math.Abs(x - _dragPress.X) > 4;
        if (_dragging && RegionExtent(project.Sequence.Tracks[_dragLane]) is var (start, _))
        {
            var raw = (long)Math.Round((x - _dragPress.X) * project.Sequence.Ppqn.TicksPerQuarterNote / PixelsPerQuarter);
            var target = Math.Max(0, SnapRegion(project.Sequence, start + raw));
            _dragDelta = target - start;
            Cursor = new Cursor(_dragCopy ? StandardCursorType.DragCopy : StandardCursorType.SizeWestEast);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (lane, delta, copy, dragged) = (_dragLane, _dragDelta, _dragCopy, _dragging);
        _dragLane = -1;
        _dragDelta = 0;
        _dragging = false;
        e.Pointer.Capture(null);
        Cursor = Cursor.Default;
        if (lane >= 0 && dragged && delta != 0)
        {
            RegionDragged?.Invoke(this, (lane, delta, copy));
        }

        InvalidateVisual();
    }

    /// <summary>Regions snap to beats when beats are wide enough to aim at, otherwise to bars.</summary>
    private long SnapRegion(Sequence sequence, long tick)
    {
        var meter = sequence.MeterMap;
        var bar = meter.BarStart(new Tick(Math.Max(0, tick)));
        var beat = meter.SignatureAt(bar).TryGetTicksPerBeat(sequence.Ppqn, out var span) ? span.Value : sequence.Ppqn.TicksPerQuarterNote;
        if (TickToX(beat) >= 24)
        {
            return bar.Value + ((long)Math.Round((tick - bar.Value) / (double)beat) * beat);
        }

        var next = meter.NextBarStart(bar);
        return tick - bar.Value < next.Value - tick ? bar.Value : next.Value;
    }

    /// <summary>The span of a track's clips, from the start of the first to the end of the last.</summary>
    private static (long Start, long End)? RegionExtent(Track track) =>
        track.Clips.IsEmpty ? null : (track.Clips[0].Start.Value, track.Clips[^1].End.Value);

    private void DrawRegion(DrawingContext context, Track track, int lane, double left, double right)
    {
        if (lane == _dragLane && _dragging && _dragDelta != 0)
        {
            // The original stays in place (dimmed unless copying) under the clips being dragged.
            using (context.PushOpacity(_dragCopy ? 1 : 0.35))
            {
                DrawClips(context, track, lane, left, right, 0);
            }

            DrawClips(context, track, lane, left, right, _dragDelta);
            return;
        }

        DrawClips(context, track, lane, left, right, 0);
    }

    private void DrawClips(DrawingContext context, Track track, int lane, double left, double right, long offset)
    {
        // One pitch range for the whole track, so a note sits at the same height in every clip.
        var notes = track.ArrangedEvents.OfType<NoteEvent>().ToList();
        var low = notes.Count == 0 ? 0 : notes.Min(n => n.Note.Value);
        var high = Math.Max(low + 12, notes.Count == 0 ? 0 : notes.Max(n => n.Note.Value));
        var first = 0;
        foreach (var clip in track.Clips)
        {
            // Notes are in timeline order and clips do not overlap, so each clip's notes follow the last clip's.
            while (first < notes.Count && notes[first].Position < clip.Start)
            {
                first++;
            }

            var end = first;
            while (end < notes.Count && notes[end].Position < clip.End)
            {
                end++;
            }

            DrawClip(context, track, clip, notes, (first, end), (low, high), lane, left, right, offset);
            first = end;
        }
    }

    // notes[slice] are this clip's notes, in timeline order.
    private void DrawClip(DrawingContext context, Track track, Clip clip, List<NoteEvent> notes, (int Start, int End) slice, (int Low, int High) range, int lane, double left, double right, long offset)
    {
        var shift = TickToX(offset);
        var x0 = Math.Round(TickToX(clip.Start.Value) + shift) + 1;
        var x1 = Math.Round(TickToX(clip.End.Value) + shift);
        if (x1 < left || x0 > right)
        {
            return;
        }

        var color = Palette.Track(lane);
        var selected = SelectedLanes.Contains(lane);
        var muted = track.IsMuted;
        var top = (lane * LaneHeight) + 2;
        var height = LaneHeight - 5;
        var body = new Rect(x0, top, Math.Max(3, x1 - x0 - 1), height);
        var fill = Palette.Mix(Palette.Lane, color, muted ? 0.16 : selected ? 0.42 : 0.32);
        var header = Palette.Mix(Palette.Lane, color, muted ? 0.32 : selected ? 0.88 : 0.68);
        var edge = selected ? Palette.Lighten(color, 0.45) : Palette.Darken(color, 0.25);

        context.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(edge), 1), body, 2, 2);
        context.DrawRectangle(new SolidColorBrush(header), null, new Rect(body.X + 0.5, body.Y + 0.5, body.Width - 1, 13), 1.5, 1.5);

        // The name stays readable at the left edge of the view while the clip scrolls past.
        var labelX = Math.Max(body.X + 4, VisibleLeft + 4);
        if (labelX < body.Right - 24)
        {
            var name = new FormattedText(clip.Name.Length > 0 ? clip.Name : track.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, RegionFace, 10, RegionText)
            {
                MaxTextWidth = Math.Max(1, body.Right - labelX - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(name, new Point(labelX, body.Y + 0.5));
        }

        if (slice.Start == slice.End)
        {
            return;
        }

        var (low, high) = range;
        var noteTop = body.Y + 16;
        var noteArea = body.Bottom - noteTop - 3;
        var span = high - low + 1;
        var noteHeight = Math.Clamp(noteArea / span, 1.5, 4);
        var noteColor = muted ? Palette.Mix(color, Palette.Lane, 0.4) : Palette.Lighten(color, selected ? 0.6 : 0.45);
        var firstTick = Math.Max(clip.Start.Value, XToTick(Math.Max(0, left - shift)));
        var lastVisible = Math.Min(clip.End.Value - 1, XToTick(Math.Max(0, right - shift)));
        using (context.PushClip(body))
        {
            for (var i = slice.Start; i < slice.End; i++)
            {
                var note = notes[i];
                if (note.Position.Value > lastVisible)
                {
                    break;
                }

                if (note.EndPosition.Value < firstTick)
                {
                    continue;
                }

                var x = TickToX(note.Position.Value) + shift;
                var w = Math.Max(1.5, TickToX(note.EndPosition.Value) - x - 0.5);
                var y = noteTop + ((high - note.Note.Value) * (noteArea - noteHeight) / Math.Max(1, span - 1));
                var opacity = 0.55 + (0.45 * note.Velocity.Value / 127.0);
                context.FillRectangle(new SolidColorBrush(noteColor, opacity), new Rect(x, y, w, noteHeight));
            }
        }
    }

    private void DrawTake(DrawingContext context, int lane)
    {
        var preview = RecordingPreview;
        var start = preview.Min(n => n.Start);
        var end = preview.Max(n => n.End);
        var top = (lane * LaneHeight) + 2;
        var body = new Rect(TickToX(start), top, Math.Max(3, TickToX(end) - TickToX(start)), LaneHeight - 5);
        context.DrawRectangle(RecordFill, RecordEdge, body, 2, 2);

        var low = preview.Min(n => n.Note.Value);
        var high = Math.Max(low + 12, preview.Max(n => n.Note.Value));
        var span = high - low + 1;
        var area = body.Height - 8;
        foreach (var note in preview)
        {
            var x = TickToX(note.Start);
            var y = body.Y + 4 + ((high - note.Note.Value) * (area - 3) / Math.Max(1, span - 1));
            context.FillRectangle(RecordNote, new Rect(x, y, Math.Max(1.5, TickToX(note.End) - x), 3));
        }
    }
}
