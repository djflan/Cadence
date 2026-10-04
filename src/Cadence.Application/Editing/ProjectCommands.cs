using Cadence.Domain.Projects;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Application.Editing;

/// <summary>The editing operations Cadence offers, as undoable commands.</summary>
public static class ProjectCommands
{
    public static IProjectCommand RenameProject(string name) =>
        new ProjectCommand("Rename Project", p => p.Name == name ? p : p with { Name = name });

    public static IProjectCommand AddTrack(Track track) =>
        new ProjectCommand("Add Track", p => p with { Sequence = p.Sequence.WithTrack(track) });

    /// <summary>Removes a track and its route.</summary>
    public static IProjectCommand RemoveTrack(TrackId track) =>
        new ProjectCommand("Delete Track", p => p.Sequence.FindTrack(track) is null
            ? p
            : p with { Sequence = p.Sequence.WithoutTrack(track), Routing = p.Routing.Without(track) });

    public static IProjectCommand RenameTrack(TrackId track, string name) =>
        EditTrack("Rename Track", track, t => t.Name == name ? t : t.WithName(name));

    public static IProjectCommand SetMuted(TrackId track, bool muted) =>
        EditTrack(muted ? "Mute Track" : "Unmute Track", track, t => t.IsMuted == muted ? t : t.WithMuted(muted));

    public static IProjectCommand SetSoloed(TrackId track, bool soloed) =>
        EditTrack(soloed ? "Solo Track" : "Unsolo Track", track, t => t.IsSoloed == soloed ? t : t.WithSoloed(soloed));

    public static IProjectCommand AddEvent(TrackId track, TrackEvent trackEvent) =>
        EditTrack(trackEvent is NoteEvent ? "Add Note" : "Add Event", track, t => t.Add(trackEvent));

    public static IProjectCommand RemoveEvent(TrackId track, EventId id) =>
        EditTrack("Delete Event", track, t => t.Remove(id));

    public static IProjectCommand ReplaceEvent(TrackId track, TrackEvent trackEvent) =>
        EditTrack("Edit Event", track, t => t.Replace(trackEvent));

    /// <summary>Sets a route, replacing any existing route for the same track.</summary>
    public static IProjectCommand SetRoute(TrackRoute route) =>
        new ProjectCommand("Change Routing", p => Equals(p.Routing.Find(route.Track), route) ? p : p with { Routing = p.Routing.With(route) });

    public static IProjectCommand SetLoop(TickRange? loop) =>
        new ProjectCommand(loop is null ? "Clear Loop" : "Set Loop", p => Equals(p.Loop, loop) ? p : p with { Loop = loop });

    /// <summary>Sets the tempo in effect from <paramref name="position"/> onward.</summary>
    public static IProjectCommand SetTempo(Tick position, Tempo tempo) =>
        new ProjectCommand("Change Tempo", p => p.Sequence.TempoMap.TempoAt(position) == tempo && p.Sequence.TempoMap.Changes.Any(c => c.Position == position)
            ? p
            : p with { Sequence = p.Sequence.WithTempoMap(p.Sequence.TempoMap.With(new TempoChange(position, tempo))) });

    /// <summary>Applies several commands as one undoable step.</summary>
    public static IProjectCommand Batch(string label, IEnumerable<IProjectCommand> commands)
    {
        var list = commands.ToList();
        return new ProjectCommand(label, p => list.Aggregate(p, (current, command) => command.Apply(current)));
    }

    /// <summary>Removes tracks and their routes in one step.</summary>
    public static IProjectCommand RemoveTracks(IReadOnlyCollection<TrackId> tracks) =>
        Batch(tracks.Count == 1 ? "Delete Track" : "Delete Tracks", tracks.Select(RemoveTrack));

    /// <summary>
    /// Copies each track (with fresh track and event IDs, and its route) directly below the original.
    /// </summary>
    public static IProjectCommand DuplicateTracks(IReadOnlyCollection<TrackId> tracks) =>
        new ProjectCommand(tracks.Count == 1 ? "Duplicate Track" : "Duplicate Tracks", p =>
        {
            var result = p;
            foreach (var id in tracks)
            {
                if (result.Sequence.FindTrack(id) is not { } track)
                {
                    continue;
                }

                var name = track.Name.Length + 5 <= Track.MaxNameLength ? track.Name + " copy" : track.Name;
                var copy = new Track(TrackId.New(), name, track.Events.Select(e => e with { Id = EventId.New() }), track.IsMuted, track.IsSoloed);
                var index = result.Sequence.Tracks.IndexOf(track) + 1;
                result = result with { Sequence = result.Sequence.InsertTrack(index, copy) };
                if (result.Routing.Find(id) is { } route)
                {
                    result = result with { Routing = result.Routing.With(route with { Track = copy.Id }) };
                }
            }

            return result;
        });

    /// <summary>Mutes or unmutes several tracks in one step.</summary>
    public static IProjectCommand SetMuted(IReadOnlyCollection<TrackId> tracks, bool muted) =>
        Batch(muted ? "Mute Tracks" : "Unmute Tracks", tracks.Select(t => SetMuted(t, muted)));

    public static IProjectCommand SetSoloed(IReadOnlyCollection<TrackId> tracks, bool soloed) =>
        Batch(soloed ? "Solo Tracks" : "Unsolo Tracks", tracks.Select(t => SetSoloed(t, soloed)));

    private static ProjectCommand EditTrack(string label, TrackId id, Func<Track, Track> edit) =>
        new(label, p =>
        {
            if (p.Sequence.FindTrack(id) is not { } track)
            {
                return p;
            }

            var edited = edit(track);
            return ReferenceEquals(edited, track) ? p : p with { Sequence = p.Sequence.WithTrack(edited) };
        });
}
