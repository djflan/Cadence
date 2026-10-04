using System.Collections.Immutable;
using Cadence.Domain.Time;

namespace Cadence.Domain.Sequencing;

/// <summary>
/// An immutable track: a name, mute/solo state, and events in canonical order. Every edit returns a
/// new track, so a prepared playback snapshot can never observe a half-applied change.
/// </summary>
/// <remarks>
/// Events are kept sorted by position, then <see cref="EventPhase"/>. Events that tie on both keep
/// the order in which they were supplied or inserted (a newly inserted event goes after its ties).
/// </remarks>
public sealed class Track
{
    public const int MaxNameLength = 256;

    private Track(TrackId id, string name, ImmutableArray<TrackEvent> events, bool isMuted, bool isSoloed)
    {
        Id = id;
        Name = name;
        Events = events;
        IsMuted = isMuted;
        IsSoloed = isSoloed;
    }

    /// <exception cref="ArgumentException">Two events share an <see cref="EventId"/>, or the name is too long.</exception>
    public Track(TrackId id, string name, IEnumerable<TrackEvent> events, bool isMuted = false, bool isSoloed = false)
        : this(id, ValidateName(name), Canonicalize(events), isMuted, isSoloed)
    {
    }

    public static Track Create(string name) => new(TrackId.New(), name, []);

    public TrackId Id { get; }

    public string Name { get; }

    public ImmutableArray<TrackEvent> Events { get; }

    public bool IsMuted { get; }

    public bool IsSoloed { get; }

    /// <summary>The latest tick affected by any event (note releases included), or zero if empty.</summary>
    public Tick EndPosition => Events.IsEmpty ? Tick.Zero : Events.Max(e => e.EndPosition);

    public Track WithName(string name) => new(Id, ValidateName(name), Events, IsMuted, IsSoloed);

    public Track WithMuted(bool isMuted) => new(Id, Name, Events, isMuted, IsSoloed);

    public Track WithSoloed(bool isSoloed) => new(Id, Name, Events, IsMuted, isSoloed);

    /// <summary>Replaces every event at once, keeping the track's identity, name, and mute/solo state.</summary>
    /// <exception cref="ArgumentException">Two events share an <see cref="EventId"/>.</exception>
    public Track WithEvents(IEnumerable<TrackEvent> events) => new(Id, Name, Canonicalize(events), IsMuted, IsSoloed);

    public TrackEvent? Find(EventId id)
    {
        foreach (var e in Events)
        {
            if (e.Id == id)
            {
                return e;
            }
        }

        return null;
    }

    /// <exception cref="ArgumentException">An event with the same ID already exists.</exception>
    public Track Add(TrackEvent trackEvent)
    {
        ArgumentNullException.ThrowIfNull(trackEvent);
        if (Find(trackEvent.Id) is not null)
        {
            throw new ArgumentException($"Event {trackEvent.Id} is already on this track.", nameof(trackEvent));
        }

        return new Track(Id, Name, Events.Insert(UpperBound(Events, trackEvent), trackEvent), IsMuted, IsSoloed);
    }

    /// <summary>Removes the event with <paramref name="id"/>, or returns this track unchanged if absent.</summary>
    public Track Remove(EventId id)
    {
        var index = IndexOf(id);
        return index < 0 ? this : new Track(Id, Name, Events.RemoveAt(index), IsMuted, IsSoloed);
    }

    /// <summary>Replaces the event with the same ID, re-sorting it as a fresh insertion.</summary>
    /// <exception cref="KeyNotFoundException">No event has that ID.</exception>
    public Track Replace(TrackEvent trackEvent)
    {
        ArgumentNullException.ThrowIfNull(trackEvent);
        var index = IndexOf(trackEvent.Id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Event {trackEvent.Id} is not on this track.");
        }

        var without = Events.RemoveAt(index);
        return new Track(Id, Name, without.Insert(UpperBound(without, trackEvent), trackEvent), IsMuted, IsSoloed);
    }

    private int IndexOf(EventId id)
    {
        for (var i = 0; i < Events.Length; i++)
        {
            if (Events[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static int UpperBound(ImmutableArray<TrackEvent> events, TrackEvent candidate)
    {
        int low = 0, high = events.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (EventOrder.Compare(events[mid], candidate) <= 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static ImmutableArray<TrackEvent> Canonicalize(IEnumerable<TrackEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var list = events.ToList();
        var ids = new HashSet<EventId>();
        foreach (var e in list)
        {
            ArgumentNullException.ThrowIfNull(e, nameof(events));
            if (!ids.Add(e.Id))
            {
                throw new ArgumentException($"Event {e.Id} appears more than once.", nameof(events));
            }
        }

        // OrderBy is stable, preserving supplied order among ties.
        return [.. list.OrderBy(e => e.Position).ThenBy(e => e.Phase)];
    }

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length <= MaxNameLength
            ? name
            : throw new ArgumentException($"Track names are limited to {MaxNameLength} characters.", nameof(name));
    }
}
