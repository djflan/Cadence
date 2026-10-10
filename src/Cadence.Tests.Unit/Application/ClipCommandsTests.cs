using Cadence.Application.Editing;
using Cadence.Domain.Midi;
using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Tests.Unit.Application;

/// <summary>Commands that put events into clips, record takes as clips, and move or copy clips.</summary>
public sealed class ClipCommandsTests
{
    private static readonly Ppqn Ppqn = new(480);
    private static readonly MidiChannel One = MidiChannel.FromNumber(1);

    private static NoteEvent Note(long at, long length = 240) =>
        new(new Tick(at), new TickSpan(length), One, NoteNumber.MiddleC, new Velocity(100));

    // Events are at content positions; the clip has no trim.
    private static NoteClip Clip(long start, long length, params TrackEvent[] events) =>
        new(ClipId.New(), new Tick(start), new TickSpan(length), TickSpan.Zero, new EventList(events));

    private static Project With(params Clip[] clips) =>
        Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn).WithTrack(new Track(TrackId.New(), "t", clips)) };

    private static Track TrackOf(Project project) => project.Sequence.Tracks[0];

    private static List<(long Start, long End)> Bounds(Project project) =>
        [.. TrackOf(project).Clips.Select(c => (c.Start.Value, c.End.Value))];

    [Fact]
    public void EditEvents_GrowsTheClipToBarLines_ButNotIntoTheNextClip()
    {
        var first = Clip(0, 1920);
        var project = With(first, Clip(3840, 1920));
        var id = TrackOf(project).Id;

        var grown = ProjectCommands.AddEvent(id, first.Id, Note(2000)).Apply(project);
        Assert.Equal([(0L, 3840L), (3840L, 5760L)], Bounds(grown));

        var clamped = ProjectCommands.AddEvent(id, first.Id, Note(3000, length: 2000)).Apply(project);
        Assert.Equal([(0L, 3840L), (3840L, 5760L)], Bounds(clamped));
        // The note is kept whole, but plays only up to the clip's end.
        var note = Assert.IsType<NoteEvent>(Assert.Single(TrackOf(clamped).ArrangedEvents));
        Assert.Equal(840, note.Duration.Value);
        Assert.Equal(2000, ((NoteEvent)((NoteClip)TrackOf(clamped).Clips[0]).Content.Items[0]).Duration.Value);
    }

    [Fact]
    public void EditEvents_WithANewClipId_CreatesAClipInTheGap()
    {
        var project = With(Clip(0, 1000));
        var id = TrackOf(project).Id;
        var clip = ClipId.New();

        var created = ProjectCommands.AddEvent(id, clip, Note(2000)).Apply(project);

        var added = Assert.IsType<NoteClip>(TrackOf(created).FindClip(clip));
        Assert.Equal((1920L, 3840L), (added.Start.Value, added.End.Value));
        Assert.Equal([2000L], TrackOf(created).ArrangedEvents.Select(e => e.Position.Value));

        // Starting right after another clip, the new clip begins where that one ends.
        var squeezed = ProjectCommands.AddEvent(id, ClipId.New(), Note(1100)).Apply(project);
        Assert.Equal([(0L, 1000L), (1000L, 1920L)], Bounds(squeezed));
    }

    [Fact]
    public void EditEvents_WithANewClipId_WhereAClipIs_AddsToThatClip()
    {
        var existing = Clip(0, 1920);
        var project = With(existing);

        var edited = ProjectCommands.AddEvent(TrackOf(project).Id, ClipId.New(), Note(100)).Apply(project);

        Assert.Equal(existing.Id, Assert.Single(TrackOf(edited).Clips).Id);
        Assert.Single(TrackOf(edited).ArrangedEvents);
    }

    [Fact]
    public void RemoveEvents_FindsThemInAnyClip()
    {
        var a = Note(0);
        var b = Note(0);
        var project = With(Clip(0, 480, a), Clip(480, 480, b));

        var removed = ProjectCommands.RemoveEvents(TrackOf(project).Id, [a.Id, b.Id]).Apply(project);

        Assert.Empty(TrackOf(removed).ArrangedEvents);
        Assert.Equal(2, TrackOf(removed).Clips.Length);
        Assert.Same(project, ProjectCommands.RemoveEvents(TrackOf(project).Id, [EventId.New()]).Apply(project));
    }

    [Fact]
    public void Record_Overdub_JoinsTheClipsTheTakeOverlaps()
    {
        var a = Note(100);
        var b = Note(100);
        var project = With(Clip(0, 1920, a), Clip(1920, 1920, b), Clip(5760, 1920));
        var take = Note(1800, length: 400);

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [take], null).Apply(project);

        Assert.Equal([(0L, 3840L), (5760L, 7680L)], Bounds(recorded));
        Assert.Equal([a.Id, take.Id, b.Id], TrackOf(recorded).ArrangedEvents.Select(e => e.Id));
        Assert.Equal(TrackOf(project).Clips[0].Id, TrackOf(recorded).Clips[0].Id);
    }

    [Fact]
    public void Record_Overdub_IntoSpace_MakesABarAlignedClip()
    {
        var project = With();

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [Note(2000)], null).Apply(project);

        Assert.Equal([(1920L, 3840L)], Bounds(recorded));
    }

    [Fact]
    public void Record_Replace_ClearsTheRangeAndJoinsPickupNotes()
    {
        var before = Note(100);
        var inside = Note(2000);
        var project = With(Clip(0, 3840, before, inside));
        var pickup = Note(1900, length: 200);
        var take = Note(2100);

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [pickup, take], new TickRange(new Tick(1920), new Tick(3840))).Apply(project);

        // The pickup note starts before the range, so the take joins the clip it reaches into.
        Assert.Equal([(0L, 3840L)], Bounds(recorded));
        Assert.Equal([before.Id, pickup.Id, take.Id], TrackOf(recorded).ArrangedEvents.Select(e => e.Id));
    }

    [Fact]
    public void Record_ReplaceWithNothing_StillClearsTheRange()
    {
        var project = With(Clip(0, 3840, Note(100), Note(2000)));

        var recorded = ProjectCommands.Record(TrackOf(project).Id, [], new TickRange(new Tick(1920), new Tick(3840))).Apply(project);

        Assert.Equal([(0L, 1920L)], Bounds(recorded));
        Assert.Single(TrackOf(recorded).ArrangedEvents);
    }

    [Fact]
    public void MoveClips_StopsAtTickZeroAndMakesRoom()
    {
        var first = Clip(1000, 500);
        var second = Clip(2000, 500);
        var project = With(first, second);
        var id = TrackOf(project).Id;

        var moved = ClipCommands.MoveClips(id, [first.Id], -5000).Apply(project);
        Assert.Equal([(0L, 500L), (2000L, 2500L)], Bounds(moved));

        var onto = ClipCommands.MoveClips(id, [first.Id], 800).Apply(project);
        Assert.Equal([(1800L, 2300L), (2300L, 2500L)], Bounds(onto));
        Assert.Equal("Move Clip", ClipCommands.MoveClips(id, [first.Id], 1).Label);
    }

    [Fact]
    public void MoveClips_TogetherDoNotClearEachOther()
    {
        var first = Clip(0, 500);
        var second = Clip(500, 500);
        var project = With(first, second);

        var moved = ClipCommands.MoveClips(TrackOf(project).Id, [first.Id, second.Id], 250).Apply(project);

        Assert.Equal([(250L, 750L), (750L, 1250L)], Bounds(moved));
        Assert.Equal([first.Id, second.Id], TrackOf(moved).Clips.Select(c => c.Id));
    }

    [Fact]
    public void CopyClips_AddsCopiesWithNewIds()
    {
        var note = Note(0);
        var clip = Clip(0, 500, note);
        var project = With(clip);

        var copied = ClipCommands.CopyClips(TrackOf(project).Id, [clip.Id], 1000).Apply(project);

        Assert.Equal([(0L, 500L), (1000L, 1500L)], Bounds(copied));
        Assert.NotEqual(clip.Id, TrackOf(copied).Clips[1].Id);
        Assert.NotEqual(note.Id, TrackOf(copied).ArrangedEvents[1].Id);
        Assert.Equal("Copy Clips", ClipCommands.CopyClips(TrackOf(project).Id, [clip.Id, ClipId.New()], 1).Label);
        Assert.Same(project, ClipCommands.CopyClips(TrackOf(project).Id, [clip.Id], 0).Apply(project));
    }
}
