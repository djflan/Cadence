using System.Collections.Immutable;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Endpoints;
using Cadence.Profiles;

namespace Cadence.Application.Routing;

public enum EndpointBindingKind
{
    /// <summary>The route names no endpoint.</summary>
    Unassigned,

    /// <summary>The endpoint with the stored stable ID is present.</summary>
    Bound,

    /// <summary>
    /// The stored ID is gone, but exactly one endpoint from the same provider has the remembered name.
    /// Playback uses it; the UI should offer to confirm the rebinding.
    /// </summary>
    BoundByName,

    /// <summary>Nothing matches; the route is kept and waits for the endpoint or an explicit rebinding.</summary>
    Missing,

    /// <summary>Several endpoints share the remembered name; the user must choose.</summary>
    Ambiguous,
}

public enum ProfileBindingKind
{
    Unassigned,
    Resolved,
    Missing,
}

public sealed record EndpointResolution(EndpointBindingKind Kind, EndpointDescriptor? Endpoint, ImmutableArray<EndpointDescriptor> Candidates)
{
    public bool IsUsable => Kind is EndpointBindingKind.Bound or EndpointBindingKind.BoundByName;
}

public sealed record ProfileResolution(ProfileBindingKind Kind, DeviceProfile? Profile);

/// <summary>A track's route checked against what is available now, with plain-language problems.</summary>
public sealed record ResolvedRoute(TrackId Track, TrackRoute? Route, ProfileResolution Profile, EndpointResolution Endpoint, ImmutableArray<string> Problems)
{
    public bool CanPlay => Endpoint.IsUsable;
}

/// <summary>
/// Resolves stored routes against the current profile catalog and endpoints. Profiles are found by
/// ID only; endpoints by stable ID, then by remembered name within the same provider. Endpoint names
/// are never used to pick a profile, and a missing profile never prevents playback.
/// </summary>
public static class RouteResolver
{
    public static ImmutableArray<ResolvedRoute> ResolveAll(Sequence sequence, RoutingTable routes, ProfileCatalog profiles, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(routes);
        return [.. sequence.Tracks.Select(track => Resolve(track.Id, routes.Find(track.Id), profiles, endpoints))];
    }

    public static ResolvedRoute Resolve(TrackId track, TrackRoute? route, ProfileCatalog profiles, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(endpoints);
        var problems = ImmutableArray.CreateBuilder<string>();

        var profile = ResolveProfile(route?.Profile, profiles);
        if (profile.Kind == ProfileBindingKind.Missing)
        {
            problems.Add($"Profile \"{route!.Profile!.DisplayName ?? route.Profile.ProfileId}\" is not installed; notes still play, but voice names and setup messages are unavailable.");
        }

        var endpoint = ResolveEndpoint(route?.Endpoint, endpoints);
        switch (endpoint.Kind)
        {
            case EndpointBindingKind.Unassigned:
                problems.Add("No output is assigned; the track will not play.");
                break;
            case EndpointBindingKind.BoundByName:
                problems.Add($"\"{endpoint.Endpoint!.DisplayName}\" was matched by name because its ID changed. Confirm the connection to keep it.");
                break;
            case EndpointBindingKind.Missing:
                problems.Add($"\"{route!.Endpoint!.DisplayName ?? route.Endpoint.EndpointKey}\" is not connected. Reconnect it or choose another output.");
                break;
            case EndpointBindingKind.Ambiguous:
                problems.Add($"{endpoint.Candidates.Length} outputs are named \"{route!.Endpoint!.DisplayName}\". Choose one.");
                break;
        }

        if (route?.Voice is { } voice && profile.Profile is { } resolved && resolved.FindBank(voice.BankId) is null)
        {
            problems.Add($"Bank \"{voice.BankId}\" is not defined by profile \"{resolved.Name}\"; the voice will not be selected.");
        }

        return new ResolvedRoute(track, route, profile, endpoint, problems.ToImmutable());
    }

    /// <summary>Creates a stored reference for an endpoint, capturing its identity hints.</summary>
    public static EndpointReference ReferenceTo(EndpointDescriptor endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new EndpointReference(endpoint.Id.Provider, endpoint.Id.Value, endpoint.DisplayName, endpoint.Manufacturer, endpoint.Model);
    }

    public static ProfileReference ReferenceTo(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ProfileReference(profile.Id, profile.Name);
    }

    private static ProfileResolution ResolveProfile(ProfileReference? reference, ProfileCatalog profiles)
    {
        if (reference is null)
        {
            return new ProfileResolution(ProfileBindingKind.Unassigned, null);
        }

        return profiles.Find(reference.ProfileId) is { } profile
            ? new ProfileResolution(ProfileBindingKind.Resolved, profile)
            : new ProfileResolution(ProfileBindingKind.Missing, null);
    }

    private static EndpointResolution ResolveEndpoint(EndpointReference? reference, IReadOnlyList<EndpointDescriptor> endpoints)
    {
        if (reference is null)
        {
            return new EndpointResolution(EndpointBindingKind.Unassigned, null, []);
        }

        var outputs = endpoints.Where(e => e.Direction == EndpointDirection.Output).ToList();
        var exact = outputs.FirstOrDefault(e => e.Id.Provider == reference.ProviderId && e.Id.Value == reference.EndpointKey);
        if (exact is not null)
        {
            return new EndpointResolution(EndpointBindingKind.Bound, exact, []);
        }

        if (reference.DisplayName is { Length: > 0 } name)
        {
            var byName = outputs
                .Where(e => e.Id.Provider == reference.ProviderId && string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
            if (byName.Length == 1)
            {
                return new EndpointResolution(EndpointBindingKind.BoundByName, byName[0], byName);
            }

            if (byName.Length > 1)
            {
                return new EndpointResolution(EndpointBindingKind.Ambiguous, null, byName);
            }
        }

        return new EndpointResolution(EndpointBindingKind.Missing, null, []);
    }
}
