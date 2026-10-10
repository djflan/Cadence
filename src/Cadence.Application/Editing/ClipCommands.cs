using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Application.Editing;

/// <summary>Arrangement operations on clips, as undoable commands. Placed clips make room (see <see cref="Track.PlaceClip"/>).</summary>
public static class ClipCommands
{
    /// <summary>Moves clips in time by <paramref name="deltaTicks"/> (no earlier than tick 0), and to <paramref name="toTrack"/> when given.</summary>
    public static IProjectCommand MoveClips(TrackId track, IReadOnlyCollection<ClipId> clips, long deltaTicks, TrackId? toTrack = null) =>
        Shift(clips.Count == 1 ? "Move Clip" : "Move Clips", track, clips, deltaTicks, toTrack, copy: false);

    /// <summary>Copies clips <paramref name="deltaTicks"/> later (or earlier, no earlier than tick 0), onto <paramref name="toTrack"/> when given.</summary>
    public static IProjectCommand CopyClips(TrackId track, IReadOnlyCollection<ClipId> clips, long deltaTicks, TrackId? toTrack = null) =>
        Shift(clips.Count == 1 ? "Copy Clip" : "Copy Clips", track, clips, deltaTicks, toTrack, copy: true);

    /// <summary>Repeats clips right after themselves: copies shifted by the span from the first clip's start to the last clip's end.</summary>
    public static IProjectCommand DuplicateClips(TrackId track, IReadOnlyCollection<ClipId> clips) =>
        new ProjectCommand(clips.Count == 1 ? "Duplicate Clip" : "Duplicate Clips", p =>
        {
            var chosen = p.Sequence.FindTrack(track)?.Clips.Where(c => clips.Contains(c.Id)).ToList() ?? [];
            return chosen.Count == 0 ? p : CopyClips(track, clips, chosen[^1].End.Value - chosen[0].Start.Value).Apply(p);
        });

    public static IProjectCommand DeleteClips(TrackId track, IReadOnlyCollection<ClipId> clips) =>
        ProjectCommands.EditTrack(clips.Count == 1 ? "Delete Clip" : "Delete Clips", track, t => clips.Aggregate(t, (acc, id) => acc.RemoveClip(id)));

    /// <summary>Splits each of <paramref name="clips"/> that <paramref name="at"/> falls strictly inside.</summary>
    public static IProjectCommand SplitClips(TrackId track, IReadOnlyCollection<ClipId> clips, Tick at) =>
        ProjectCommands.EditTrack(clips.Count == 1 ? "Split Clip" : "Split Clips", track, t =>
        {
            var split = t.Clips.SelectMany<Clip, Clip>(c =>
            {
                if (!clips.Contains(c.Id) || at <= c.Start || at >= c.End)
                {
                    return [c];
                }

                var (left, right) = c.SplitAt(at, ClipId.New());
                return [left, right];
            }).ToList();
            return split.Count == t.Clips.Length ? t : t.WithClips(split);
        });

    /// <summary>
    /// Moves a clip's edges, revealing or hiding content. The clip keeps at least one tick and stops at
    /// its neighbours rather than covering them.
    /// </summary>
    public static IProjectCommand ResizeClip(TrackId track, ClipId clip, Tick start, Tick until) =>
        ProjectCommands.EditTrack("Resize Clip", track, t =>
        {
            if (t.FindClip(clip) is not { } target)
            {
                return t;
            }

            var before = t.Clips.Where(c => c.End <= target.Start).Select(c => c.End).DefaultIfEmpty(Tick.Zero).Max();
            var after = t.Clips.Where(c => c.Start >= target.End).Select(c => (Tick?)c.Start).Min();
            var from = Tick.Max(before, Tick.Min(start, new Tick(target.End.Value - 1)));
            var to = Tick.Max(new Tick(from.Value + 1), after is { } next ? Tick.Min(until, next) : until);
            return from == target.Start && to == target.End ? t : t.WithClip(target.WithBounds(from, to));
        });

    /// <summary>Adds an empty note clip, within the free space around its start.</summary>
    public static IProjectCommand CreateClip(TrackId track, NoteClip clip) =>
        ProjectCommands.EditTrack("Create Clip", track, t =>
        {
            if (t.ClipAt(clip.Start) is not null)
            {
                return t;
            }

            var (_, until) = t.GapAt(clip.Start);
            return t.WithClip(until is { } next && next < clip.End ? clip.WithBounds(clip.Start, next) : clip);
        });

    private static ProjectCommand Shift(string label, TrackId trackId, IReadOnlyCollection<ClipId> clipIds, long deltaTicks, TrackId? toTrack, bool copy) =>
        new(label, p =>
        {
            if (p.Sequence.FindTrack(trackId) is not { } track)
            {
                return p;
            }

            var destination = toTrack is { } to && to != trackId ? p.Sequence.FindTrack(to) : track;
            var clips = track.Clips.Where(c => clipIds.Contains(c.Id)).ToList();
            var delta = clips.Count == 0 ? 0 : Math.Max(deltaTicks, -clips[0].Start.Value);
            if (destination is null || (delta == 0 && ReferenceEquals(destination, track)))
            {
                return p;
            }

            // Moving clips leave first, so they cannot clear each other where they land.
            var source = copy ? track : clips.Aggregate(track, (t, c) => t.RemoveClip(c.Id));
            var target = ReferenceEquals(destination, track) ? source : destination;
            foreach (var clip in clips)
            {
                var start = new Tick(clip.Start.Value + delta);
                target = target.PlaceClip(copy ? clip.CopyTo(start) : clip with { Start = start });
            }

            var sequence = p.Sequence.WithTrack(ReferenceEquals(destination, track) ? target : source);
            return p with { Sequence = ReferenceEquals(destination, track) ? sequence : sequence.WithTrack(target) };
        });
}
