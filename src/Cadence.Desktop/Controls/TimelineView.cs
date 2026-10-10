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
/// The arrangement: one lane per track, with each of the track's clips drawn as a coloured block
/// (named on a header strip) containing its notes. Drawn directly with the GPU-backed renderer and
/// culled to the visible region, so dense projects stay smooth.
/// </summary>
/// <remarks>
/// Click a clip to select it (Shift adds, Cmd/Ctrl toggles) and drag to move the selection, also to
/// other tracks; hold Alt (Option) to copy. Drag a clip's edge to trim or extend it. Double-click a
/// clip to edit it, or empty space to create a one-bar clip. Click empty space to select the track.
/// </remarks>
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
    public static readonly StyledProperty<IReadOnlySet<ClipId>> SelectedClipsProperty = AvaloniaProperty.Register<TimelineView, IReadOnlySet<ClipId>>(nameof(SelectedClips), new HashSet<ClipId>());

    private const double EdgeGrip = 5;

    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor MoveCursor = new(StandardCursorType.DragMove);
    private static readonly Cursor CopyCursor = new(StandardCursorType.DragCopy);

    private static readonly IBrush LaneBrush = new SolidColorBrush(Palette.Lane);
    private static readonly IBrush LaneAltBrush = new SolidColorBrush(Palette.LaneAlt);
    private static readonly IBrush LaneSelected = new SolidColorBrush(Palette.LaneSelected);
    private static readonly IPen LaneDivider = new Pen(new SolidColorBrush(Palette.LaneDivider), 1);
    private static readonly IBrush RecordFill = new SolidColorBrush(Palette.Record, 0.28);
    private static readonly IPen RecordEdge = new Pen(new SolidColorBrush(Palette.Record, 0.9), 1);
    private static readonly IBrush RecordNote = new SolidColorBrush(Palette.Lighten(Palette.Record, 0.35));
    private static readonly IBrush RegionText = new SolidColorBrush(Color.Parse("#141416"));
    private static readonly Typeface RegionFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    // Clip drag state
    private readonly List<(Track Track, Clip Clip, List<NoteEvent> Notes, (int Start, int End) Slice, (int Low, int High) Range, int Lane)> _moving = [];
    private Grip _drag;
    private Point _dragPress;
    private int _dragLane;
    private Clip? _dragClip;
    private long _dragDelta;
    private int _dragLanes;
    private bool _dragCopy;
    private bool _dragging;

    private enum Grip
    {
        None,
        Body,
        StartEdge,
        EndEdge,
    }

    static TimelineView()
    {
        AffectsRender<TimelineView>(ProjectProperty, PixelsPerQuarterProperty, LaneHeightProperty, SelectedLanesProperty, VisibleLeftProperty, VisibleWidthProperty, RecordingLaneProperty, RecordingPreviewProperty, SelectedClipsProperty);
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

    /// <summary>The clips to highlight as selected.</summary>
    public IReadOnlySet<ClipId> SelectedClips
    {
        get => GetValue(SelectedClipsProperty);
        set => SetValue(SelectedClipsProperty, value);
    }

    /// <summary>Raised with the lane index and modifiers when empty space in a lane is clicked.</summary>
    public event EventHandler<(int Lane, KeyModifiers Modifiers)>? LaneClicked;

    /// <summary>Raised with the lane, clip, and modifiers when a clip is pressed, before any drag.</summary>
    public event EventHandler<(int Lane, ClipId Clip, KeyModifiers Modifiers)>? ClipPressed;

    public event EventHandler<(int Lane, ClipId Clip)>? ClipDoubleClicked;

    /// <summary>Raised with the lane and tick when empty space in a lane is double-clicked.</summary>
    public event EventHandler<(int Lane, long Tick)>? EmptyDoubleClicked;

    /// <summary>Raised when the selected clips are dragged: the snapped shift in ticks, in lanes, and whether to copy.</summary>
    public event EventHandler<(long DeltaTicks, int LaneDelta, bool Copy)>? ClipsDragged;

    /// <summary>Raised when a clip's edge is dragged, with its new bounds in ticks.</summary>
    public event EventHandler<(ClipId Clip, long Start, long End)>? ClipResized;

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

        _moving.Clear();
        for (var lane = 0; lane < lanes; lane++)
        {
            var bottom = Math.Round((lane + 1) * LaneHeight) - 0.5;
            context.DrawLine(LaneDivider, new Point(left, bottom), new Point(right, bottom));
            DrawClips(context, sequence.Tracks[lane], lane, left, right);
        }

        // Dragged clips go on top of every lane, where they would land.
        foreach (var (track, clip, notes, slice, range, lane) in _moving)
        {
            DrawClip(context, track, clip, notes, slice, range, lane, Math.Clamp(lane + _dragLanes, 0, lanes - 1), left, right, _dragDelta);
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

        var (lane, clip, grip) = HitTest(project.Sequence, point.Position);
        if (lane < 0)
        {
            return;
        }

        Focus();
        e.Handled = true;
        if (clip is null)
        {
            if (e.ClickCount >= 2)
            {
                EmptyDoubleClicked?.Invoke(this, (lane, XToTick(point.Position.X)));
            }
            else
            {
                LaneClicked?.Invoke(this, (lane, e.KeyModifiers));
            }

            return;
        }

        if (e.ClickCount >= 2)
        {
            ClipDoubleClicked?.Invoke(this, (lane, clip.Id));
            return;
        }

        ClipPressed?.Invoke(this, (lane, clip.Id, e.KeyModifiers));
        if (grip == Grip.Body && !SelectedClips.Contains(clip.Id))
        {
            // The press took the clip out of the selection; there is nothing to drag.
            return;
        }

        (_drag, _dragPress, _dragLane, _dragClip) = (grip, point.Position, lane, clip);
        (_dragDelta, _dragLanes, _dragCopy, _dragging) = (0, 0, e.KeyModifiers.HasFlag(KeyModifiers.Alt), false);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Project is not { } project)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (_drag == Grip.None || !ReferenceEquals(e.Pointer.Captured, this))
        {
            // Show where an edge can be grabbed.
            SetCursor(HitTest(project.Sequence, position).Grip is Grip.StartEdge or Grip.EndEdge ? ResizeCursor : Cursor.Default);
            return;
        }

        if (_dragClip is not { } clip || _dragLane >= project.Sequence.Tracks.Length || project.Sequence.Tracks[_dragLane].FindClip(clip.Id) is null)
        {
            // The clip went away mid-drag (for example, undo).
            EndDrag();
            return;
        }

        _dragging |= Math.Abs(position.X - _dragPress.X) > 4 || Math.Abs(position.Y - _dragPress.Y) > LaneHeight / 2;
        if (!_dragging)
        {
            return;
        }

        var sequence = project.Sequence;
        var raw = (long)Math.Round((position.X - _dragPress.X) * sequence.Ppqn.TicksPerQuarterNote / PixelsPerQuarter);
        var track = sequence.Tracks[_dragLane];
        switch (_drag)
        {
            case Grip.Body:
                // The selection moves as one: no clip before tick 0, none past the first or last lane.
                var (earliest, firstLane, lastLane) = SelectionExtent(sequence);
                _dragDelta = Math.Max(SnapClip(sequence, clip.Start.Value + raw) - clip.Start.Value, -earliest);
                _dragLanes = Math.Clamp((int)Math.Floor(position.Y / LaneHeight) - _dragLane, -firstLane, sequence.Tracks.Length - 1 - lastLane);
                SetCursor(_dragCopy ? CopyCursor : MoveCursor);
                break;
            case Grip.StartEdge:
                // Edges stop at neighbouring clips, as the resize does.
                var before = track.Clips.Where(c => c.End <= clip.Start).Select(c => c.End.Value).DefaultIfEmpty(0).Max();
                _dragDelta = Math.Clamp(SnapClip(sequence, clip.Start.Value + raw), before, clip.End.Value - 1) - clip.Start.Value;
                break;
            case Grip.EndEdge:
                var after = track.Clips.Where(c => c.Start >= clip.End).Select(c => c.Start.Value).DefaultIfEmpty(long.MaxValue).Min();
                _dragDelta = Math.Clamp(SnapClip(sequence, clip.End.Value + raw), clip.Start.Value + 1, after) - clip.End.Value;
                break;
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (grip, clip, delta, lanes, copy, dragged) = (_drag, _dragClip, _dragDelta, _dragLanes, _dragCopy, _dragging);
        e.Pointer.Capture(null);
        EndDrag();
        if (!dragged || clip is null)
        {
            return;
        }

        switch (grip)
        {
            case Grip.Body when delta != 0 || lanes != 0:
                ClipsDragged?.Invoke(this, (delta, lanes, copy));
                break;
            case Grip.StartEdge when delta != 0:
                ClipResized?.Invoke(this, (clip.Id, clip.Start.Value + delta, clip.End.Value));
                break;
            case Grip.EndEdge when delta != 0:
                ClipResized?.Invoke(this, (clip.Id, clip.Start.Value, clip.End.Value + delta));
                break;
        }
    }

    private void EndDrag()
    {
        (_drag, _dragClip, _dragDelta, _dragLanes, _dragging) = (Grip.None, null, 0, 0, false);
        SetCursor(Cursor.Default);
        InvalidateVisual();
    }

    private void SetCursor(Cursor cursor)
    {
        if (!ReferenceEquals(Cursor, cursor))
        {
            Cursor = cursor;
        }
    }

    /// <summary>The earliest start among the selected clips, and the first and last lanes holding any.</summary>
    private (long Earliest, int FirstLane, int LastLane) SelectionExtent(Sequence sequence)
    {
        var (earliest, first, last) = (long.MaxValue, int.MaxValue, -1);
        for (var lane = 0; lane < sequence.Tracks.Length; lane++)
        {
            foreach (var clip in sequence.Tracks[lane].Clips)
            {
                if (SelectedClips.Contains(clip.Id))
                {
                    (earliest, first, last) = (Math.Min(earliest, clip.Start.Value), Math.Min(first, lane), lane);
                }
            }
        }

        return last < 0 ? (0, 0, sequence.Tracks.Length - 1) : (earliest, first, last);
    }

    /// <summary>The lane and clip under <paramref name="point"/>, and which part of the clip: its body or an edge.</summary>
    /// <remarks>
    /// Edges can be grabbed a few pixels either side. Where two clips touch, the side of the line the
    /// pointer is on decides which clip's edge it is.
    /// </remarks>
    private (int Lane, Clip? Clip, Grip Grip) HitTest(Sequence sequence, Point point)
    {
        var lane = (int)Math.Floor(point.Y / LaneHeight);
        if (lane < 0 || lane >= sequence.Tracks.Length)
        {
            return (-1, null, Grip.None);
        }

        var track = sequence.Tracks[lane];
        var tick = new Tick(Math.Max(0, XToTick(point.X)));
        if (track.ClipAt(tick) is { } clip)
        {
            var (x0, x1) = (TickToX(clip.Start.Value), TickToX(clip.End.Value));

            // Edges of clips too narrow to hold both are not offered, so the clip can still be dragged.
            var roomy = x1 - x0 > 4 * EdgeGrip;
            var grip = !roomy ? Grip.Body : point.X - x0 <= EdgeGrip ? Grip.StartEdge : x1 - point.X <= EdgeGrip ? Grip.EndEdge : Grip.Body;
            return (lane, clip, grip);
        }

        var (from, until) = track.GapAt(tick);
        if (from > Tick.Zero && point.X - TickToX(from.Value) <= EdgeGrip && track.ClipAt(new Tick(from.Value - 1)) is { } before)
        {
            return (lane, before, Grip.EndEdge);
        }

        if (until is { } next && TickToX(next.Value) - point.X <= EdgeGrip && track.ClipAt(next) is { } after)
        {
            return (lane, after, Grip.StartEdge);
        }

        return (lane, null, Grip.None);
    }

    /// <summary>Clips snap to beats when beats are wide enough to aim at, otherwise to bars.</summary>
    private long SnapClip(Sequence sequence, long tick)
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

    private bool IsMoving => _dragging && _drag == Grip.Body && (_dragDelta != 0 || _dragLanes != 0);

    private void DrawClips(DrawingContext context, Track track, int lane, double left, double right)
    {
        // One pitch range for the whole track, so a note sits at the same height in every clip.
        var notes = track.ArrangedEvents.OfType<NoteEvent>().ToList();
        var low = notes.Count == 0 ? 0 : notes.Min(n => n.Note.Value);
        var range = (low, Math.Max(low + 12, notes.Count == 0 ? 0 : notes.Max(n => n.Note.Value)));
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

            var slice = (first, end);
            first = end;
            if (_dragging && _drag is Grip.StartEdge or Grip.EndEdge && clip.Id == _dragClip?.Id)
            {
                // The clip as it will be: trimmed content hidden, or hidden content revealed.
                var (start, until) = _drag == Grip.StartEdge ? (clip.Start.Value + _dragDelta, clip.End.Value) : (clip.Start.Value, clip.End.Value + _dragDelta);
                var resized = clip.WithBounds(new Tick(start), new Tick(until));
                var shown = resized is NoteClip noteClip ? noteClip.Arrange().OfType<NoteEvent>().ToList() : [];
                DrawClip(context, track, resized, shown, (0, shown.Count), range, lane, lane, left, right, 0);
                continue;
            }

            if (IsMoving && SelectedClips.Contains(clip.Id))
            {
                // The original stays in place (dimmed unless copying) under the clip being dragged.
                _moving.Add((track, clip, notes, slice, range, lane));
                using (context.PushOpacity(_dragCopy ? 1 : 0.35))
                {
                    DrawClip(context, track, clip, notes, slice, range, lane, lane, left, right, 0);
                }

                continue;
            }

            DrawClip(context, track, clip, notes, slice, range, lane, lane, left, right, 0);
        }
    }

    // notes[slice] are this clip's notes, in timeline order. The clip has its track's colour
    // (colorLane) wherever it is drawn (drawLane).
    private void DrawClip(DrawingContext context, Track track, Clip clip, List<NoteEvent> notes, (int Start, int End) slice, (int Low, int High) range, int colorLane, int drawLane, double left, double right, long offset)
    {
        var shift = TickToX(offset);
        var x0 = Math.Round(TickToX(clip.Start.Value) + shift) + 1;
        var x1 = Math.Round(TickToX(clip.End.Value) + shift);
        if (x1 < left || x0 > right)
        {
            return;
        }

        var color = Palette.Track(colorLane);
        var selected = SelectedClips.Contains(clip.Id);
        var muted = track.IsMuted;
        var top = (drawLane * LaneHeight) + 2;
        var height = LaneHeight - 5;
        var body = new Rect(x0, top, Math.Max(3, x1 - x0 - 1), height);
        var fill = Palette.Mix(Palette.Lane, color, muted ? 0.16 : selected ? 0.5 : 0.32);
        var header = Palette.Mix(Palette.Lane, color, muted ? 0.32 : selected ? 0.95 : 0.68);
        var edge = selected ? new Pen(Brushes.White, 1.5) : new Pen(new SolidColorBrush(Palette.Darken(color, 0.25)), 1);

        context.DrawRectangle(new SolidColorBrush(fill), edge, body, 2, 2);
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
