using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Time;

namespace Cadence.Domain.Sequencing;

/// <summary>
/// An immutable track: a name, mute/solo state, and clips. A track has no type; its clips decide what
/// it holds (ADR 0020). Every edit returns a new track, so a prepared playback snapshot can never
/// observe a half-applied change.
/// </summary>
/// <remarks>
/// Clips are sorted by start and never overlap, and event IDs are unique across all of a track's
/// clips.
/// </remarks>
public sealed class Track
{
    public const int MaxNameLength = 256;

    // Computed on first use. ImmutableArray is a single reference, written atomically.
    private ImmutableArray<TrackEvent> _arranged;

    private Track(TrackId id, string name, ImmutableArray<Clip> clips, bool isMuted, bool isSoloed)
    {
        Id = id;
        Name = name;
        Clips = clips;
        IsMuted = isMuted;
        IsSoloed = isSoloed;
    }

    /// <exception cref="ArgumentException">
    /// Two clips overlap or share an ID, two events share an <see cref="EventId"/>, or the name is too long.
    /// </exception>
    public Track(TrackId id, string name, IEnumerable<Clip> clips, bool isMuted = false, bool isSoloed = false)
        : this(id, ValidateName(name), ValidateClips(clips), isMuted, isSoloed)
    {
    }

    public static Track Create(string name) => new(TrackId.New(), name, []);

    /// <summary>
    /// A track holding <paramref name="events"/> (at timeline positions) in one clip from tick 0 to just
    /// after the last event ends, or no clip if there are none.
    /// </summary>
    /// <exception cref="ArgumentException">Two events share an <see cref="EventId"/>, or the name is too long.</exception>
    public static Track FromEvents(TrackId id, string name, IEnumerable<TrackEvent> events, bool isMuted = false, bool isSoloed = false)
    {
        var content = new EventList(events);
        if (content.Items.IsEmpty)
        {
            return new Track(id, name, [], isMuted, isSoloed);
        }

        var end = Math.Max(content.EndPosition.Value, content.Items[^1].Position.Value + 1);
        return new Track(id, name, [new NoteClip(ClipId.New(), Tick.Zero, new TickSpan(end), TickSpan.Zero, content)], isMuted, isSoloed);
    }

    public TrackId Id { get; }

    public string Name { get; }

    /// <summary>Clips in timeline order.</summary>
    public ImmutableArray<Clip> Clips { get; }

    public bool IsMuted { get; }

    public bool IsSoloed { get; }

    /// <summary>What the track plays: the visible events of its note clips at timeline positions, in canonical order.</summary>
    public ImmutableArray<TrackEvent> ArrangedEvents
    {
        get
        {
            if (_arranged.IsDefault)
            {
                _arranged = [.. Clips.OfType<NoteClip>().SelectMany(c => c.Arrange())];
            }

            return _arranged;
        }
    }

    /// <summary>The end of the last clip, or zero if there are none.</summary>
    public Tick EndPosition => Clips.IsEmpty ? Tick.Zero : Clips[^1].End;

    /// <summary>The channel of the first channel event, which is the track's channel unless its route overrides it.</summary>
    public MidiChannel? FirstChannel
    {
        get
        {
            foreach (var clip in Clips)
            {
                if (clip is not NoteClip notes)
                {
                    continue;
                }

                foreach (var e in notes.Content.Items)
                {
                    if (e is ChannelEvent channelEvent)
                    {
                        return channelEvent.Channel;
                    }
                }
            }

            return null;
        }
    }

    public Track WithName(string name) => new(Id, ValidateName(name), Clips, IsMuted, IsSoloed);

    public Track WithMuted(bool isMuted) => new(Id, Name, Clips, isMuted, IsSoloed);

    public Track WithSoloed(bool isSoloed) => new(Id, Name, Clips, IsMuted, isSoloed);

    /// <exception cref="ArgumentException">The clips overlap or share IDs, or two events share an ID.</exception>
    public Track WithClips(IEnumerable<Clip> clips) => new(Id, Name, ValidateClips(clips), IsMuted, IsSoloed);

    public Clip? FindClip(ClipId id)
    {
        foreach (var clip in Clips)
        {
            if (clip.Id == id)
            {
                return clip;
            }
        }

        return null;
    }

    /// <summary>The clip containing <paramref name="position"/>, if any.</summary>
    public Clip? ClipAt(Tick position)
    {
        var index = LastStartingAtOrBefore(position);
        return index >= 0 && Clips[index].Contains(position) ? Clips[index] : null;
    }

    /// <summary>
    /// The free space around <paramref name="position"/>: from the end of the clip before it (or tick 0) to
    /// the start of the clip after it (or null if there is none). Meaningful only where no clip is.
    /// </summary>
    public (Tick From, Tick? Until) GapAt(Tick position)
    {
        var index = LastStartingAtOrBefore(position);
        var from = index >= 0 ? Clips[index].End : Tick.Zero;
        Tick? until = index + 1 < Clips.Length ? Clips[index + 1].Start : null;
        return (from, until);
    }

    /// <summary>The note clip holding the event with <paramref name="id"/>, if any.</summary>
    public NoteClip? ClipOf(EventId id)
    {
        foreach (var clip in Clips)
        {
            if (clip is NoteClip notes && notes.Content.Find(id) is not null)
            {
                return notes;
            }
        }

        return null;
    }

    /// <summary>Replaces the clip with the same ID, or adds it if new.</summary>
    /// <exception cref="ArgumentException">The clip would overlap another, or its events' IDs are already on the track.</exception>
    public Track WithClip(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return WithClips(Clips.Where(c => c.Id != clip.Id).Append(clip));
    }

    public Track RemoveClip(ClipId id) =>
        FindClip(id) is null ? this : new Track(Id, Name, Clips.RemoveAll(c => c.Id == id), IsMuted, IsSoloed);

    /// <summary>
    /// Places <paramref name="clip"/>, replacing any clip with its ID. Clips it would overlap make room:
    /// they are removed, trimmed, or split (see <see cref="ClearRange"/>).
    /// </summary>
    public Track PlaceClip(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return RemoveClip(clip.Id).ClearRange(clip.Start, clip.End).WithClip(clip);
    }

    /// <summary>
    /// Empties [<paramref name="from"/>, <paramref name="to"/>): clips inside it are removed, and clips
    /// reaching into it are trimmed (non-destructively) or split around it.
    /// </summary>
    public Track ClearRange(Tick from, Tick to)
    {
        if (to <= from || !Clips.Any(c => c.Overlaps(from, to)))
        {
            return this;
        }

        var result = new List<Clip>(Clips.Length + 1);
        foreach (var clip in Clips)
        {
            if (!clip.Overlaps(from, to))
            {
                result.Add(clip);
            }
            else if (clip.Start < from && clip.End > to)
            {
                var (left, right) = clip.SplitAt(from, ClipId.New());
                result.Add(left);
                result.Add(right.WithBounds(to, right.End));
            }
            else if (clip.Start < from)
            {
                result.Add(clip.WithBounds(clip.Start, from));
            }
            else if (clip.End > to)
            {
                result.Add(clip.WithBounds(to, clip.End));
            }
        }

        return new Track(Id, Name, [.. result], IsMuted, IsSoloed);
    }

    private int LastStartingAtOrBefore(Tick position)
    {
        int low = 0, high = Clips.Length - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Clips[mid].Start <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    private static ImmutableArray<Clip> ValidateClips(IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var sorted = clips.ToList();
        foreach (var clip in sorted)
        {
            ArgumentNullException.ThrowIfNull(clip, nameof(clips));
        }

        sorted.Sort((a, b) => a.Start.CompareTo(b.Start));
        var clipIds = new HashSet<ClipId>();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!clipIds.Add(sorted[i].Id))
            {
                throw new ArgumentException($"Clip {sorted[i].Id} appears more than once.", nameof(clips));
            }

            if (i > 0 && sorted[i - 1].End > sorted[i].Start)
            {
                throw new ArgumentException($"Clips {sorted[i - 1].Id} and {sorted[i].Id} overlap.", nameof(clips));
            }
        }

        // Each clip's events are already unique; only clips together can repeat an ID.
        var noteClips = sorted.OfType<NoteClip>().ToList();
        if (noteClips.Count > 1)
        {
            var eventIds = new HashSet<EventId>();
            foreach (var e in noteClips.SelectMany(c => c.Content.Items))
            {
                if (!eventIds.Add(e.Id))
                {
                    throw new ArgumentException($"Event {e.Id} appears in more than one clip.", nameof(clips));
                }
            }
        }

        return [.. sorted];
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length <= MaxNameLength
            ? name
            : throw new ArgumentException($"Track names are limited to {MaxNameLength} characters.", nameof(name));
    }
}
