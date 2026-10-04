using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;

namespace Cadence.Domain.Routing;

/// <summary>
/// A reference to a device profile by its stable ID. The display name is remembered so a missing
/// profile can still be named to the user.
/// </summary>
public sealed record ProfileReference
{
    public ProfileReference(string profileId, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ProfileId = profileId.Length <= 100 ? profileId : throw new ArgumentException("Profile IDs are at most 100 characters.", nameof(profileId));
        DisplayName = displayName;
    }

    public string ProfileId { get; }

    public string? DisplayName { get; }
}

/// <summary>
/// Stable identity hints for a MIDI endpoint: the provider and its stable key (for example a CoreMIDI
/// unique ID), plus the name, manufacturer, and model last seen. Never a port index, and never used
/// to decide which device profile applies.
/// </summary>
public sealed record EndpointReference
{
    public EndpointReference(string providerId, string endpointKey, string? displayName = null, string? manufacturer = null, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        ProviderId = providerId;
        EndpointKey = endpointKey;
        DisplayName = displayName;
        Manufacturer = manufacturer;
        Model = model;
    }

    public string ProviderId { get; }

    public string EndpointKey { get; }

    public string? DisplayName { get; }

    public string? Manufacturer { get; }

    public string? Model { get; }
}

/// <summary>A voice chosen from a profile: a bank by its profile-local ID and a program in it.</summary>
public sealed record VoiceAssignment(string BankId, ProgramNumber Program);

/// <summary>
/// How one track reaches an instrument: which profile describes the instrument (what it understands),
/// which endpoint carries the messages (where they go), and optional per-route transforms. Profile and
/// endpoint are independent; either may be missing without affecting the other or the track's music.
/// </summary>
public sealed record TrackRoute
{
    public const int MaxTranspose = 48;

    private readonly int _transpose;

    public TrackRoute(TrackId track) => Track = track;

    public TrackId Track { get; init; }

    public ProfileReference? Profile { get; init; }

    public EndpointReference? Endpoint { get; init; }

    /// <summary>When set, every channel message on the track is sent on this channel.</summary>
    public MidiChannel? Channel { get; init; }

    /// <summary>Semitones added to every note, from -48 to 48.</summary>
    public int Transpose
    {
        get => _transpose;
        init => _transpose = value is >= -MaxTranspose and <= MaxTranspose
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Transpose), value, "Transpose must be between -48 and 48 semitones.");
    }

    /// <summary>A voice selected at the start of playback, before the track's own events.</summary>
    public VoiceAssignment? Voice { get; init; }
}

/// <summary>The routes of a project, keyed by track. Immutable.</summary>
public sealed class RoutingTable
{
    private RoutingTable(ImmutableDictionary<TrackId, TrackRoute> routes) => Routes = routes;

    public static RoutingTable Empty { get; } = new(ImmutableDictionary<TrackId, TrackRoute>.Empty);

    public ImmutableDictionary<TrackId, TrackRoute> Routes { get; }

    public static RoutingTable From(IEnumerable<TrackRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var builder = ImmutableDictionary.CreateBuilder<TrackId, TrackRoute>();
        foreach (var route in routes)
        {
            ArgumentNullException.ThrowIfNull(route, nameof(routes));
            if (builder.ContainsKey(route.Track))
            {
                throw new ArgumentException($"Track {route.Track} has more than one route.", nameof(routes));
            }

            builder.Add(route.Track, route);
        }

        return new RoutingTable(builder.ToImmutable());
    }

    public TrackRoute? Find(TrackId track) => Routes.GetValueOrDefault(track);

    public RoutingTable With(TrackRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new RoutingTable(Routes.SetItem(route.Track, route));
    }

    public RoutingTable Without(TrackId track) => new(Routes.Remove(track));
}
