using Cadence.Application.Editing;
using Cadence.Application.Plugins;
using Cadence.Application.Sessions;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Presentation;
using Cadence.Profiles;
using static Cadence.Tests.Integration.Plugins.PluginTestHost;

namespace Cadence.Tests.Integration.Plugins;

/// <summary>
/// Scenario 11 as the musician sees it: a plugin added from the device strip runs in a real worker; when the
/// worker is killed, Cadence keeps running, the strip shows the device as crashed with a restart action, and
/// restarting brings it back. Nothing is mocked.
/// </summary>
[Collection(PluginProcessTests.Name)]
public sealed class PluginDeviceStripTests
{
    [Fact]
    public async Task AKilledPluginWorker_ShowsAsCrashedInTheStrip_AndCanBeRestarted()
    {
        await using var host = new PluginTestHost();
        var clock = new VirtualClock(TimeSpan.FromSeconds(1));
        using var provider = new LoopbackMidiProvider(clock);
        using var endpoints = new EndpointDirectory([provider]);
        var session = new ProjectSession();
        session.Execute(ProjectCommands.AddTrack(Track.Create("Vocals", TrackRole.Audio)));
        var bridge = new PluginDeviceHost(host.Manager, session, [Gain], maxBlockFrames: 64);
        var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, clock, startThread: false);
        var vm = new MainViewModel(session, playback, endpoints, new NoUi(), new Immediate(), plugins: bridge);
        try
        {
            await vm.InitializeAsync();
            var strip = vm.DeviceStrip;
            strip.DeviceToAdd = strip.AvailableDevices.Single(d => d.Name == "Reference Gain");
            strip.AddDeviceCommand.Execute(null);
            await WaitUntilAsync(() => strip.Devices.Count == 1 && strip.Devices[0].Status == "Running", StatusTimeout, "the plugin to run");
            var device = strip.Devices[0];
            var instance = bridge.InstanceOf(device.Id)!;
            host.Track(instance);
            Assert.Equal("Reference Gain", device.Title);

            KillProcess(instance.WorkerProcessId!.Value);
            await WaitUntilAsync(() => device.Status == "Crashed", StatusTimeout, "the strip to show the crash");

            Assert.Equal("[Reference Gain: Crashed]", device.Title);
            Assert.True(device.NeedsAttention);
            Assert.True(device.CanRestart);
            Assert.Single(session.Project.ChainOf(session.Project.Sequence.Tracks[0].Id)!.Devices);

            await device.RestartCommand.ExecuteAsync(null);
            host.Track(instance);
            await WaitUntilAsync(() => device.Status == "Running", StatusTimeout, "the strip to show the restart");
            Assert.False(device.CanRestart);
        }
        finally
        {
            await vm.DisposeAsync();
        }
    }

    private sealed class Immediate : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class NoUi : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
