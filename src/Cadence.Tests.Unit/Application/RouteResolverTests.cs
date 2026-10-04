using System.Text;
using Cadence.Application.Routing;
using Cadence.Domain.Midi;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Endpoints;
using Cadence.Profiles;

namespace Cadence.Tests.Unit.Application;

public sealed class RouteResolverTests
{
    private static readonly EndpointDescriptor SynthOut = Output("coremidi", "1001", "Synth");
    private static readonly EndpointDescriptor ModuleOut = Output("coremidi", "1002", "Module");

    internal static EndpointDescriptor Output(string provider, string key, string name) =>
        new(new EndpointId(provider, key), name, EndpointDirection.Output, EndpointTransport.Physical, EndpointCapabilities.SystemExclusive);

    internal static ProfileCatalog Catalog(params string[] ids) =>
        ids.Aggregate(ProfileCatalog.Empty, (catalog, id) => catalog.Add(id, ProfileLoader.Load(Encoding.UTF8.GetBytes($$"""
            { "format": "cadence-device-profile", "schemaVersion": 1, "id": "{{id}}", "version": "1", "name": "Profile {{id}}",
              "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
              "banks": [ { "id": "main", "name": "Main", "msb": 0, "lsb": 3 } ] }
            """))));

    private static TrackRoute Route(EndpointReference? endpoint = null, ProfileReference? profile = null) =>
        new(TrackId.New()) { Endpoint = endpoint, Profile = profile };

    [Fact]
    public void ExactId_Binds()
    {
        var resolved = RouteResolver.Resolve(TrackId.New(), Route(RouteResolver.ReferenceTo(SynthOut)), ProfileCatalog.Empty, [SynthOut, ModuleOut]);

        Assert.Equal(EndpointBindingKind.Bound, resolved.Endpoint.Kind);
        Assert.Same(SynthOut, resolved.Endpoint.Endpoint);
        Assert.True(resolved.CanPlay);
        Assert.Empty(resolved.Problems);
    }

    [Fact]
    public void ChangedId_WithUniqueName_BindsByNameAndAsksForConfirmation()
    {
        var reference = new EndpointReference("coremidi", "old-id", "synth");

        var resolved = RouteResolver.Resolve(TrackId.New(), Route(reference), ProfileCatalog.Empty, [SynthOut, ModuleOut]);

        Assert.Equal(EndpointBindingKind.BoundByName, resolved.Endpoint.Kind);
        Assert.Same(SynthOut, resolved.Endpoint.Endpoint);
        Assert.True(resolved.CanPlay);
        Assert.Contains("matched by name", Assert.Single(resolved.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedName_IsAmbiguous()
    {
        var twin = Output("coremidi", "1003", "Synth");

        var resolved = RouteResolver.Resolve(TrackId.New(), Route(new EndpointReference("coremidi", "gone", "Synth")), ProfileCatalog.Empty, [SynthOut, twin]);

        Assert.Equal(EndpointBindingKind.Ambiguous, resolved.Endpoint.Kind);
        Assert.Equal(2, resolved.Endpoint.Candidates.Length);
        Assert.False(resolved.CanPlay);
    }

    [Fact]
    public void NameMatching_StaysWithinTheSameProvider()
    {
        var resolved = RouteResolver.Resolve(TrackId.New(), Route(new EndpointReference("winmm", "3", "Synth")), ProfileCatalog.Empty, [SynthOut]);

        Assert.Equal(EndpointBindingKind.Missing, resolved.Endpoint.Kind);
        Assert.Contains("\"Synth\" is not connected", Assert.Single(resolved.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void InputEndpoints_AreNeverBoundAsOutputs()
    {
        var input = SynthOut with { Direction = EndpointDirection.Input };

        Assert.Equal(EndpointBindingKind.Missing, RouteResolver.Resolve(TrackId.New(), Route(RouteResolver.ReferenceTo(SynthOut)), ProfileCatalog.Empty, [input]).Endpoint.Kind);
    }

    [Fact]
    public void MissingProfile_IsExplainedButDoesNotPreventPlayback()
    {
        var resolved = RouteResolver.Resolve(TrackId.New(), Route(RouteResolver.ReferenceTo(SynthOut), new ProfileReference("absent.profile", "Old Synth")), ProfileCatalog.Empty, [SynthOut]);

        Assert.Equal(ProfileBindingKind.Missing, resolved.Profile.Kind);
        Assert.True(resolved.CanPlay);
        Assert.Contains("\"Old Synth\" is not installed", Assert.Single(resolved.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ProfilesAndEndpointsCombineFreely()
    {
        // The same profile can drive any endpoint, and an endpoint's name never selects a profile.
        var catalog = Catalog("vendor.module", "generic.gm");
        var profile = new ProfileReference("vendor.module");
        foreach (var endpoint in new[] { SynthOut, ModuleOut, Output("loopback", "bus/out", "Profile vendor.module") })
        {
            var resolved = RouteResolver.Resolve(TrackId.New(), Route(RouteResolver.ReferenceTo(endpoint), profile), catalog, [endpoint]);

            Assert.Equal("vendor.module", resolved.Profile.Profile!.Id);
            Assert.Same(endpoint, resolved.Endpoint.Endpoint);
        }

        var unprofiled = RouteResolver.Resolve(TrackId.New(), Route(RouteResolver.ReferenceTo(ModuleOut)), catalog, [ModuleOut]);
        Assert.Equal(ProfileBindingKind.Unassigned, unprofiled.Profile.Kind);
    }

    [Fact]
    public void UnknownVoiceBank_IsReported()
    {
        var route = Route(RouteResolver.ReferenceTo(SynthOut), new ProfileReference("p.one")) with { Voice = new VoiceAssignment("nope", new ProgramNumber(0)) };

        var resolved = RouteResolver.Resolve(route.Track, route, Catalog("p.one"), [SynthOut]);

        Assert.Contains(resolved.Problems, p => p.Contains("Bank \"nope\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveAll_CoversEveryTrackIncludingUnrouted()
    {
        var routed = Track.Create("routed");
        var unrouted = Track.Create("unrouted");
        var sequence = Sequence.CreateEmpty(Cadence.Domain.Time.Ppqn.Default).WithTrack(routed).WithTrack(unrouted);
        var routes = RoutingTable.Empty.With(new TrackRoute(routed.Id) { Endpoint = RouteResolver.ReferenceTo(SynthOut) });

        var resolved = RouteResolver.ResolveAll(sequence, routes, ProfileCatalog.Empty, [SynthOut]);

        Assert.Equal([EndpointBindingKind.Bound, EndpointBindingKind.Unassigned], resolved.Select(r => r.Endpoint.Kind));
        Assert.Contains("No output", Assert.Single(resolved[1].Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void RoutingTable_IsImmutableAndRejectsDuplicateTracks()
    {
        var track = TrackId.New();
        var table = RoutingTable.Empty.With(new TrackRoute(track));

        Assert.Empty(RoutingTable.Empty.Routes);
        Assert.Empty(table.Without(track).Routes);
        Assert.Throws<ArgumentException>(() => RoutingTable.From([new TrackRoute(track), new TrackRoute(track)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrackRoute(track) { Transpose = 49 });
    }
}
