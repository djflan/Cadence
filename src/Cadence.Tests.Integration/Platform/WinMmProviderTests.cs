using System.Runtime.Versioning;
using Cadence.Midi.Endpoints;
using Cadence.Platform.Windows;

namespace Cadence.Tests.Integration.Platform;

/// <summary>
/// Exercises the real WinMM stack on Windows. These tests never send a sounding message: they only
/// open software synthesizers and send messages that must be rejected, or send after closing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinMmProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WinMM is only available on Windows.");
        }
    }

    private static EndpointDescriptor RequireSoftwareSynth(WinMmProvider provider)
    {
        var synth = provider.GetEndpoints().FirstOrDefault(e => e.Transport == EndpointTransport.Software);
        if (synth is null)
        {
            Assert.Skip("No WinMM software synthesizer is installed.");
        }

        return synth;
    }

    [Fact]
    public void Provider_EnumeratesOutputsWithUniqueIds()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        var endpoints = provider.GetEndpoints();

        foreach (var endpoint in endpoints)
        {
            TestContext.Current.SendDiagnosticMessage($"{endpoint.Id} ({endpoint.Transport})");
        }

        Assert.All(endpoints, e =>
        {
            Assert.Equal(WinMmProvider.ProviderId, e.Id.Provider);
            Assert.Equal(EndpointDirection.Output, e.Direction);
            Assert.Equal(EndpointCapabilities.None, e.Capabilities);
        });
        Assert.Equal(endpoints.Count, endpoints.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public async Task OpenUnknownOutput_Throws()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenOutputAsync(new EndpointId(WinMmProvider.ProviderId, "No such device"), Ct));
    }

    [Fact]
    public async Task OpenInput_IsNotSupported()
    {
        RequireWindows();
        using var provider = new WinMmProvider();

        await Assert.ThrowsAsync<EndpointUnavailableException>(
            async () => await provider.OpenInputAsync(new EndpointId(WinMmProvider.ProviderId, "Any"), Ct));
    }

    [Fact]
    public async Task Output_RejectsInvalidAndSysEx_AndClosesOnDispose()
    {
        RequireWindows();
        using var provider = new WinMmProvider();
        var synth = RequireSoftwareSynth(provider);
        var states = new List<EndpointState>();

        var output = await provider.OpenOutputAsync(synth.Id, Ct);
        output.StateChanged += (_, e) => states.Add(e.State);

        Assert.Equal(EndpointState.Open, output.State);
        Assert.Equal(SendResult.Rejected, output.Send([0x90, 0x3C], MidiTimestamp.Immediate));
        Assert.Equal(SendResult.Rejected, output.Send([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7], MidiTimestamp.Immediate));

        output.Dispose();

        Assert.Equal(EndpointState.Closed, output.State);
        Assert.Equal(SendResult.Closed, output.Send([0xB0, 0x7B, 0x00], MidiTimestamp.Immediate));
        Assert.Equal([EndpointState.Closed], states);
    }

    [Fact]
    public async Task DisposingProvider_ClosesOpenOutputs()
    {
        RequireWindows();
        var provider = new WinMmProvider();
        var synth = RequireSoftwareSynth(provider);
        var output = await provider.OpenOutputAsync(synth.Id, Ct);

        provider.Dispose();

        Assert.Equal(EndpointState.Closed, output.State);
        Assert.Throws<ObjectDisposedException>(provider.GetEndpoints);
    }
}
