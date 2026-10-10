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

    public static IProjectCommand AddEvent(TrackId track, ClipId clip, TrackEvent trackEvent) =>
        EditEvents(track, clip, trackEvent is NoteEvent ? "Add Note" : "Add Event", [], [trackEvent]);

    public static IProjectCommand RemoveEvent(TrackId track, EventId id) => RemoveEvents(track, [id]);

    public static IProjectCommand ReplaceEvent(TrackId track, ClipId clip, TrackEvent trackEvent) =>
        EditEvents(track, clip, "Edit Event", [], [trackEvent]);

    /// <summary>
    /// Removes <paramref name="remove"/> from one clip and adds <paramref name="add"/> (at timeline
    /// positions) to it in a single step. An event in both is replaced; events not in the clip are
    /// ignored when removing.
    /// </summary>
    /// <remarks>
    /// The clip grows, to bar lines, to show what was added, but never into a neighbouring clip or before
    /// tick 0; anything still outside it is kept hidden. If the track has no clip with that ID, one is
    /// created around the added events in the gap where they start (or, if a clip is already there,
    /// they go into that clip).
    /// </remarks>
    public static IProjectCommand EditEvents(TrackId track, ClipId clip, string label, IEnumerable<EventId> remove, IEnumerable<TrackEvent> add)
    {
        var removed = remove.ToHashSet();
        var added = add.ToList();
        removed.UnionWith(added.Select(e => e.Id));
        return EditTrack(label, track, (t, meter) =>
        {
            switch (t.FindClip(clip))
            {
                case NoteClip target:
                    if (added.Count == 0 && !target.Content.Items.Any(e => removed.Contains(e.Id)))
                    {
                        return t;
                    }

                    return t.WithClip(GrowToShow(target.EditTimeline(removed, added), added, t, meter));
                case null when added.Count > 0 && t.ClipAt(added.Min(e => e.Position)) is NoteClip existing:
                    // Something already sits where the new clip would start; add to that clip instead.
                    return t.WithClip(GrowToShow(existing.EditTimeline(removed, added), added, t, meter));
                case null when added.Count > 0:
                    var created = NoteClip.Enclosing(added, meter, clip)!;
                    var (from, until) = Gap(t, created.Start, created.End, added.Min(e => e.Position));
                    return t.WithClip(created.WithBounds(from, until));
                default:
                    return t;
            }
        });
    }

    /// <summary>Replaces events (matched by ID) with edited versions, e.g. the result of an <see cref="EventEdits"/> operation.</summary>
    public static IProjectCommand ReplaceEvents(TrackId track, ClipId clip, string label, IReadOnlyCollection<TrackEvent> events) =>
        events.Count == 0 ? new ProjectCommand(label, p => p) : EditEvents(track, clip, label, [], events);

    public static IProjectCommand AddEvents(TrackId track, ClipId clip, string label, IReadOnlyCollection<TrackEvent> events) =>
        EditEvents(track, clip, label, [], events);

    /// <summary>Removes events from whichever of the track's clips hold them.</summary>
    public static IProjectCommand RemoveEvents(TrackId track, IReadOnlyCollection<EventId> events) =>
        EditTrack(events.Count == 1 ? "Delete Event" : "Delete Events", track, (t, _) =>
        {
            var ids = events.ToHashSet();
            var clips = t.Clips.Select(c => c is NoteClip notes && notes.Content.Items.Any(e => ids.Contains(e.Id))
                ? notes.EditTimeline(ids, [])
                : c).ToList();
            return clips.SequenceEqual(t.Clips) ? t : t.WithClips(clips);
        });

    /// <summary>
    /// Adds a recorded take to a track as a clip. With <paramref name="replaceRange"/>, that range is cleared
    /// first (replace recording). The take's clip then joins any clips it overlaps, keeping their visible
    /// events, so overdubbing never loses anything.
    /// </summary>
    public static IProjectCommand Record(TrackId track, IReadOnlyCollection<TrackEvent> take, TickRange? replaceRange) =>
        EditTrack("Record", track, (t, meter) =>
        {
            var cleared = replaceRange is { } range ? t.ClearRange(range.Start, range.End) : t;
            if (take.Count == 0)
            {
                return cleared;
            }

            var first = take.Min(e => e.Position.Value);
            var reach = Math.Max(take.Max(e => e.EndPosition.Value), take.Max(e => e.Position.Value) + 1);
            var (from, until) = replaceRange is { } r
                ? (Math.Min(first, r.Start.Value), Math.Max(reach, r.End.Value))
                : Bars(NoteClip.EnclosingBounds(first, reach - 1, reach, meter));
            var joined = cleared.Clips.OfType<NoteClip>().Where(c => c.Overlaps(new Tick(from), new Tick(until))).ToList();
            if (joined.Count > 0)
            {
                from = Math.Min(from, joined[0].Start.Value);
                until = Math.Max(until, joined[^1].End.Value);
            }

            var events = joined.SelectMany(c => c.Arrange()).Concat(take).Select(e => e with { Position = new Tick(e.Position.Value - from) });
            var clip = new NoteClip(joined.Count > 0 ? joined[0].Id : ClipId.New(), new Tick(from), new TickSpan(until - from), TickSpan.Zero, new EventList(events));
            return cleared.PlaceClip(clip);
        });

    /// <summary>Sets a route, replacing any existing route for the same track.</summary>
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

    /// <summary>Sets the time signature from <paramref name="position"/> (a bar line) onward.</summary>
    /// <exception cref="ArgumentException">A beat of this signature is not a whole number of ticks at the project's resolution.</exception>
    public static IProjectCommand SetTimeSignature(Tick position, TimeSignature signature) =>
        new ProjectCommand("Change Time Signature", p =>
        {
            var meter = p.Sequence.MeterMap;
            if (meter.Changes.Any(c => c.Position == position && c.Signature == signature))
            {
                return p;
            }

            var changes = meter.Changes.Where(c => c.Position != position).Append(new MeterChange(position, signature));
            return p with { Sequence = p.Sequence.WithMeterMap(new MeterMap(meter.Ppqn, changes)) };
        });

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
    /// Copies each track (with fresh track, clip, and event IDs, and its route) directly below the original.
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
                var copy = new Track(TrackId.New(), name, track.Clips.Select(c => c.CopyTo(c.Start)), track.IsMuted, track.IsSoloed);
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
        EditTrack(label, id, (track, _) => edit(track));

    private static ProjectCommand EditTrack(string label, TrackId id, Func<Track, MeterMap, Track> edit) =>
        new(label, p =>
        {
            if (p.Sequence.FindTrack(id) is not { } track)
            {
                return p;
            }

            var edited = edit(track, p.Sequence.MeterMap);
            return ReferenceEquals(edited, track) ? p : p with { Sequence = p.Sequence.WithTrack(edited) };
        });

    /// <summary>Grows <paramref name="clip"/> to bar lines around <paramref name="added"/>, within its gap on <paramref name="track"/>.</summary>
    private static NoteClip GrowToShow(NoteClip clip, List<TrackEvent> added, Track track, MeterMap meter)
    {
        if (added.Count == 0)
        {
            return clip;
        }

        var first = added.Min(e => e.Position.Value);
        var last = added.Max(e => e.Position.Value);
        var reach = Math.Max(added.Max(e => e.EndPosition.Value), last + 1);
        if (first >= clip.Start.Value && reach <= clip.End.Value)
        {
            return clip;
        }

        var (barStart, barEnd) = NoteClip.EnclosingBounds(first, last, reach, meter);
        var (from, until) = Gap(track.RemoveClip(clip.Id), Tick.Min(clip.Start, barStart), Tick.Max(clip.End, barEnd), clip.Start);
        return clip.WithBounds(from, until);
    }

    /// <summary>[<paramref name="from"/>, <paramref name="until"/>) narrowed to the free space on <paramref name="track"/> around <paramref name="anchor"/>.</summary>
    private static (Tick From, Tick Until) Gap(Track track, Tick from, Tick until, Tick anchor)
    {
        foreach (var other in track.Clips)
        {
            if (other.End <= anchor)
            {
                from = Tick.Max(from, other.End);
            }
            else if (other.Start > anchor)
            {
                until = Tick.Min(until, other.Start);
            }
        }

        return (from, until);
    }

    private static (long From, long Until) Bars((Tick Start, Tick End) bounds) => (bounds.Start.Value, bounds.End.Value);
}
