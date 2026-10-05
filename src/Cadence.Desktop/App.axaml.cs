using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cadence.Application.Sessions;
using Cadence.Desktop.Services;
using Cadence.Desktop.Views;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Presentation;
using Cadence.Profiles;

namespace Cadence.Desktop;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var platformProviders = PlatformProviders.Create(SystemMonotonicClock.Instance).ToList();
            var viewModel = Compose(window, platformProviders);
            window.Attach(viewModel);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => Shutdown(viewModel, platformProviders);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The composition root: the only place that chooses concrete providers and services.</summary>
    public static MainViewModel Compose(MainWindow window, IEnumerable<IMidiEndpointProvider> platformProviders)
    {
        ArgumentNullException.ThrowIfNull(window);
        var clock = SystemMonotonicClock.Instance;

        // A built-in virtual bus whose output is shown in the MIDI monitor, so Cadence is useful
        // and verifiable with no hardware attached. Platform providers are added alongside it.
        var builtIn = new LoopbackMidiProvider(clock, EndpointCapabilities.ScheduledDelivery | EndpointCapabilities.SystemExclusive);
        var monitorPort = builtIn.CreatePort("Cadence Monitor", "monitor");
        var keyboard = new ComputerKeyboardProvider(clock);
        var providers = new List<IMidiEndpointProvider> { builtIn, keyboard };
        providers.AddRange(platformProviders);
        var endpoints = new EndpointDirectory(providers);

        var profiles = ProfileCatalog.LoadDirectory(Path.Combine(AppContext.BaseDirectory, "profiles"))
            .AddDirectory(UserProfileDirectory);

        var session = new ProjectSession();
        var playback = new PlaybackController(session, endpoints, profiles, clock, playbackThreadSetup: PlatformProviders.ConfigurePlaybackThread);
        var monitor = new MidiMonitor(builtIn.OpenInputAsync(monitorPort.InputId).AsTask().GetAwaiter().GetResult());
        var viewModel = new MainViewModel(session, playback, endpoints, new DialogService(window), new AvaloniaDispatcher(), monitor, new ComputerKeyboardViewModel(keyboard));
        _ = viewModel.InitializeAsync();
        return viewModel;
    }

    /// <summary>
    /// Stops playback (releasing sounding notes), closes MIDI ports, and releases platform providers.
    /// Errors are logged rather than thrown: nothing useful can be done with them while quitting.
    /// </summary>
    private static void Shutdown(MainViewModel viewModel, IEnumerable<IMidiEndpointProvider> platformProviders)
    {
        try
        {
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (var provider in platformProviders)
            {
                provider.Dispose();
            }
        }
#pragma warning disable CA1031 // Quitting must not crash; the failure is recorded instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceError($"Error while shutting down: {ex}");
        }
    }

    /// <summary>Where users put their own profiles, e.g. ~/Library/Application Support/Cadence/profiles on macOS.</summary>
    public static string UserProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cadence", "profiles");
}
