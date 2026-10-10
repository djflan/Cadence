using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Application.Editing;

/// <summary>Arrangement operations on clips, as undoable commands. Placed clips make room (see <see cref="Track.PlaceClip"/>).</summary>
public static class ClipCommands
{
    /// <summary>Moves clips in time by <paramref name="deltaTicks"/>, no earlier than tick 0.</summary>
    public static IProjectCommand MoveClips(TrackId track, IReadOnlyCollection<ClipId> clips, long deltaTicks) =>
        Shift(clips.Count == 1 ? "Move Clip" : "Move Clips", track, clips, deltaTicks, copy: false);

    /// <summary>Copies clips <paramref name="deltaTicks"/> later (or earlier, no earlier than tick 0).</summary>
    public static IProjectCommand CopyClips(TrackId track, IReadOnlyCollection<ClipId> clips, long deltaTicks) =>
        Shift(clips.Count == 1 ? "Copy Clip" : "Copy Clips", track, clips, deltaTicks, copy: true);

    private static ProjectCommand Shift(string label, TrackId trackId, IReadOnlyCollection<ClipId> clipIds, long deltaTicks, bool copy) =>
        new(label, p =>
        {
            if (p.Sequence.FindTrack(trackId) is not { } track)
            {
                return p;
            }

            var clips = track.Clips.Where(c => clipIds.Contains(c.Id)).ToList();
            var delta = clips.Count == 0 ? 0 : Math.Max(deltaTicks, -clips[0].Start.Value);
            if (delta == 0)
            {
                return p;
            }

            // Moving clips leave first, so they cannot clear each other where they land.
            var edited = copy ? track : clips.Aggregate(track, (t, c) => t.RemoveClip(c.Id));
            foreach (var clip in clips)
            {
                var start = new Tick(clip.Start.Value + delta);
                edited = edited.PlaceClip(copy ? clip.CopyTo(start) : clip with { Start = start });
            }

            return p with { Sequence = p.Sequence.WithTrack(edited) };
        });
}
