using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Time;

namespace Cadence.Domain.Sequencing;

/// <summary>
/// An immutable track: a name, mute/solo state, and events in canonical order (see
/// <see cref="EventList"/>). Every edit returns a new track, so a prepared playback snapshot can
/// never observe a half-applied change.
/// </summary>
public sealed class Track
{
    public const int MaxNameLength = 256;

    private readonly EventList _events;

    private Track(TrackId id, string name, EventList events, bool isMuted, bool isSoloed)
    {
        Id = id;
        Name = name;
        _events = events;
        IsMuted = isMuted;
        IsSoloed = isSoloed;
    }

    /// <exception cref="ArgumentException">Two events share an <see cref="EventId"/>, or the name is too long.</exception>
    public Track(TrackId id, string name, IEnumerable<TrackEvent> events, bool isMuted = false, bool isSoloed = false)
        : this(id, ValidateName(name), new EventList(events), isMuted, isSoloed)
    {
    }

    public static Track Create(string name) => new(TrackId.New(), name, []);

    public TrackId Id { get; }

    public string Name { get; }

    public ImmutableArray<TrackEvent> Events => _events.Items;

    public bool IsMuted { get; }

    public bool IsSoloed { get; }

    /// <summary>The latest tick affected by any event (note releases included), or zero if empty.</summary>
    public Tick EndPosition => _events.EndPosition;

    /// <summary>The channel of the first channel event, which is the track's channel unless its route overrides it.</summary>
    public MidiChannel? FirstChannel => Events.OfType<ChannelEvent>().FirstOrDefault()?.Channel;

    public Track WithName(string name) => new(Id, ValidateName(name), _events, IsMuted, IsSoloed);

    public Track WithMuted(bool isMuted) => new(Id, Name, _events, isMuted, IsSoloed);

    public Track WithSoloed(bool isSoloed) => new(Id, Name, _events, IsMuted, isSoloed);

    /// <summary>Replaces every event at once, keeping the track's identity, name, and mute/solo state.</summary>
    /// <exception cref="ArgumentException">Two events share an <see cref="EventId"/>.</exception>
    public Track WithEvents(IEnumerable<TrackEvent> events) => new(Id, Name, new EventList(events), IsMuted, IsSoloed);

    public TrackEvent? Find(EventId id) => _events.Find(id);

    /// <exception cref="ArgumentException">An event with the same ID already exists.</exception>
    public Track Add(TrackEvent trackEvent) => new(Id, Name, _events.Add(trackEvent), IsMuted, IsSoloed);

    /// <summary>Removes the event with <paramref name="id"/>, or returns this track unchanged if absent.</summary>
    public Track Remove(EventId id)
    {
        var events = _events.Remove(id);
        return ReferenceEquals(events, _events) ? this : new Track(Id, Name, events, IsMuted, IsSoloed);
    }

    /// <summary>Replaces the event with the same ID, re-sorting it as a fresh insertion.</summary>
    /// <exception cref="KeyNotFoundException">No event has that ID.</exception>
    public Track Replace(TrackEvent trackEvent) => new(Id, Name, _events.Replace(trackEvent), IsMuted, IsSoloed);

    private static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Length <= MaxNameLength
            ? name
            : throw new ArgumentException($"Track names are limited to {MaxNameLength} characters.", nameof(name));
    }
}
