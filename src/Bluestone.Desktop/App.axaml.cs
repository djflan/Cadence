using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Cadence.Application.Plugins;
using Cadence.Application.Sessions;
using Cadence.Desktop.Services;
using Cadence.Desktop.Views;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Plugins;
using Cadence.Plugins.Protocol;
using Cadence.Plugins.Scanning;
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
            var plugins = new PluginHostManager(new PluginHostOptions { DataDirectory = Path.Combine(Path.GetTempPath(), $"cadence-plugins-{Environment.ProcessId}") });
            var viewModel = Compose(window, platformProviders, plugins);
            window.Attach(viewModel);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => Shutdown(viewModel, platformProviders, plugins);

            // A project or MIDI file named on the command line (or by the OS file association) opens at start.
            if (desktop.Args is [var path, ..] && MainViewModel.CanOpenFile(path))
            {
                window.Opened += async (_, _) => await viewModel.OpenFileAsync(path);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The composition root: the only place that chooses concrete providers and services.</summary>
    public static MainViewModel Compose(MainWindow window, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
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
        var devices = new PluginDeviceHost(plugins, session, ScanPlugins());
        var viewModel = new MainViewModel(session, playback, endpoints, new DialogService(window), new AvaloniaDispatcher(), monitor, new ComputerKeyboardViewModel(keyboard), devices);
        _ = viewModel.InitializeAsync();
        return viewModel;
    }

    /// <summary>
    /// Stops playback (releasing sounding notes), closes MIDI ports, and releases platform providers.
    /// Errors are logged rather than thrown: nothing useful can be done with them while quitting.
    /// </summary>
    private static void Shutdown(MainViewModel viewModel, IEnumerable<IMidiEndpointProvider> platformProviders, PluginHostManager plugins)
    {
        try
        {
            viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            plugins.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    /// <summary>
    /// The plugins in <see cref="UserPluginDirectory"/>. Each module is scanned in its own scanner process, so a module
    /// that crashes the scanner cannot take Cadence with it; it is reported and skipped on later scans (ADR 0025).
    /// </summary>
    private static IReadOnlyList<PluginIdentity> ScanPlugins()
    {
        if (!Directory.Exists(UserPluginDirectory))
        {
            return [];
        }

        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cadence", "plugin-cache");
        using var scanner = new PluginScanner(new PluginScannerOptions(cache));
        var results = Task.Run(() => scanner.ScanAsync(Directory.EnumerateFiles(UserPluginDirectory))).GetAwaiter().GetResult();
        foreach (var failure in results.Where(r => r.Failure is not null))
        {
            System.Diagnostics.Trace.TraceWarning($"Plugin module {failure.ModulePath} was not loaded: {failure.Failure}");
        }

        return [.. results.SelectMany(r => r.Plugins)];
    }

    /// <summary>Where users put plugin modules, e.g. ~/Library/Application Support/Cadence/plugins on macOS.</summary>
    public static string UserPluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cadence", "plugins");

    /// <summary>Where users put their own profiles, e.g. ~/Library/Application Support/Cadence/profiles on macOS.</summary>
    public static string UserProfileDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cadence", "profiles");
}
