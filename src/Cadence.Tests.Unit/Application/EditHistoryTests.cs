using Cadence.Application.Editing;
using Cadence.Domain.Midi;
using Cadence.Domain.Projects;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Tests.Unit.Application;

public sealed class EditHistoryTests
{
    [Fact]
    public void UndoAndRedo_RestoreExactStates()
    {
        var original = Project.CreateNew("A");
        var history = new EditHistory(original);

        history.Execute(ProjectCommands.RenameProject("B"));
        var renamed = history.Current;
        history.Execute(ProjectCommands.RenameProject("C"));

        Assert.Equal("Rename Project", history.UndoLabel);
        history.Undo();
        Assert.Same(renamed, history.Current);
        history.Undo();
        Assert.Same(original, history.Current);
        Assert.False(history.CanUndo);
        history.Redo();
        history.Redo();
        Assert.Equal("C", history.Current.Name);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void NoOpCommands_AreNotRecorded()
    {
        var history = new EditHistory(Project.CreateNew("Same"));
        var changes = 0;
        history.Changed += (_, _) => changes++;

        Assert.False(history.Execute(ProjectCommands.RenameProject("Same")));
        Assert.False(history.Execute(ProjectCommands.SetMuted(TrackId.New(), true)));

        Assert.False(history.CanUndo);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void NewCommand_ClearsRedo()
    {
        var history = new EditHistory(Project.CreateNew("A"));
        history.Execute(ProjectCommands.RenameProject("B"));
        history.Undo();

        history.Execute(ProjectCommands.RenameProject("C"));

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Capacity_DropsTheOldestStates()
    {
        var history = new EditHistory(Project.CreateNew("0"), capacity: 3);
        for (var i = 1; i <= 5; i++)
        {
            history.Execute(ProjectCommands.RenameProject(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        while (history.CanUndo)
        {
            history.Undo();
        }

        Assert.Equal("2", history.Current.Name);
    }

    [Fact]
    public void TrackCommands_EditTheRightTrack()
    {
        var track = Track.Create("t");
        var history = new EditHistory(Project.CreateNew());
        var note = new NoteEvent(Tick.Zero, new TickSpan(10), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max);

        history.Execute(ProjectCommands.AddTrack(track));
        history.Execute(ProjectCommands.RenameTrack(track.Id, "Lead"));
        history.Execute(ProjectCommands.SetMuted(track.Id, true));
        history.Execute(ProjectCommands.SetSoloed(track.Id, true));
        history.Execute(ProjectCommands.AddEvent(track.Id, note));
        history.Execute(ProjectCommands.ReplaceEvent(track.Id, note with { Position = new Tick(5) }));
        history.Execute(ProjectCommands.SetRoute(new TrackRoute(track.Id) { Channel = MidiChannel.FromNumber(4) }));

        var edited = history.Current.Sequence.FindTrack(track.Id)!;
        Assert.Equal(("Lead", true, true), (edited.Name, edited.IsMuted, edited.IsSoloed));
        Assert.Equal(new Tick(5), Assert.Single(edited.Events).Position);
        Assert.NotNull(history.Current.Routing.Find(track.Id));

        history.Execute(ProjectCommands.RemoveEvent(track.Id, note.Id));
        Assert.Empty(history.Current.Sequence.FindTrack(track.Id)!.Events);

        history.Execute(ProjectCommands.RemoveTrack(track.Id));
        Assert.Empty(history.Current.Sequence.Tracks);
        Assert.Null(history.Current.Routing.Find(track.Id));
    }

    [Fact]
    public void BatchCommands_UndoAsOneStep()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var history = new EditHistory(Project.CreateNew());
        history.Execute(ProjectCommands.AddTrack(a));
        history.Execute(ProjectCommands.AddTrack(b));

        history.Execute(ProjectCommands.SetMuted([a.Id, b.Id], true));
        Assert.All(history.Current.Sequence.Tracks, t => Assert.True(t.IsMuted));
        Assert.Equal("Mute Tracks", history.UndoLabel);

        history.Undo();
        Assert.All(history.Current.Sequence.Tracks, t => Assert.False(t.IsMuted));
        Assert.False(history.Execute(ProjectCommands.SetSoloed([a.Id, b.Id], false)));
    }

    [Fact]
    public void DuplicateTracks_CopiesBelowWithFreshIdsAndRoutes()
    {
        var note = new NoteEvent(Tick.Zero, new TickSpan(10), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max);
        var a = Track.Create("Bass").Add(note);
        var b = Track.Create("Drums");
        var history = new EditHistory(Project.CreateNew());
        history.Execute(ProjectCommands.AddTrack(a));
        history.Execute(ProjectCommands.AddTrack(b));
        history.Execute(ProjectCommands.SetRoute(new TrackRoute(a.Id) { Channel = MidiChannel.FromNumber(2) }));

        history.Execute(ProjectCommands.DuplicateTracks([a.Id]));

        var tracks = history.Current.Sequence.Tracks;
        Assert.Equal(["Bass", "Bass copy", "Drums"], tracks.Select(t => t.Name));
        Assert.NotEqual(a.Id, tracks[1].Id);
        Assert.NotEqual(note.Id, tracks[1].Events.Single().Id);
        Assert.Equal(MidiChannel.FromNumber(2), history.Current.Routing.Find(tracks[1].Id)!.Channel);
    }

    [Fact]
    public void RemoveTracks_DeletesSeveralInOneStep()
    {
        var a = Track.Create("a");
        var b = Track.Create("b");
        var history = new EditHistory(Project.CreateNew());
        history.Execute(ProjectCommands.AddTrack(a));
        history.Execute(ProjectCommands.AddTrack(b));

        history.Execute(ProjectCommands.RemoveTracks([a.Id, b.Id]));
        Assert.Empty(history.Current.Sequence.Tracks);
        history.Undo();
        Assert.Equal(2, history.Current.Sequence.Tracks.Length);
    }

    [Fact]
    public void TempoAndLoopCommands_ChangeTransportSettings()
    {
        var history = new EditHistory(Project.CreateNew());

        history.Execute(ProjectCommands.SetTempo(Tick.Zero, Tempo.FromBeatsPerMinute(90)));
        history.Execute(ProjectCommands.SetLoop(new TickRange(Tick.Zero, new Tick(100))));

        Assert.Equal(Tempo.FromBeatsPerMinute(90), history.Current.Sequence.TempoMap.TempoAt(Tick.Zero));
        Assert.Equal(new Tick(100), history.Current.Loop!.End);
        Assert.False(history.Execute(ProjectCommands.SetTempo(Tick.Zero, Tempo.FromBeatsPerMinute(90))));
    }
}
