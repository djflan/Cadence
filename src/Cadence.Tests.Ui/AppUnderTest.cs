using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Cadence.Application.Plugins;
using Cadence.Application.Sessions;
using Cadence.Desktop;
using Cadence.Desktop.Services;
using Cadence.Desktop.Views;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Presentation;
using Cadence.Profiles;

namespace Cadence.Tests.Ui;

/// <summary>
/// Cadence's own <see cref="App"/> (theme, styles, fonts) on Avalonia's headless platform, rendered with Skia so frames
/// can be captured. Every test runs on the one Avalonia UI thread through <see cref="RunAsync"/>.
/// </summary>
public static class HeadlessApp
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    /// <summary>Runs <paramref name="test"/> on the UI thread and waits for it, including its awaits.</summary>
    public static Task RunAsync(Func<Task> test) =>
        Session.Value.Dispatch(
            async () =>
            {
                await test();
                return true;
            },
            TestContext.Current.CancellationToken);
}

/// <summary>
/// The real main window, attached to a view model composed as the app composes it, but with a loopback MIDI port
/// instead of the platform's and no playback thread. Dispose to close the window and release everything.
/// </summary>
internal sealed class AppUnderTest : IAsyncDisposable
{
    private AppUnderTest(MainWindow window, MainViewModel viewModel, ProjectSession session, PlaybackController playback, LoopbackPort port)
    {
        Window = window;
        ViewModel = viewModel;
        Session = session;
        Playback = playback;
        Port = port;
    }

    public MainWindow Window { get; }

    public MainViewModel ViewModel { get; }

    public ProjectSession Session { get; }

    public PlaybackController Playback { get; }

    /// <summary>A loopback output named "Synth" that tracks can be routed to.</summary>
    public LoopbackPort Port { get; }

    public static async Task<AppUnderTest> OpenAsync(ProjectSession? session = null, Func<ProjectSession, PluginDeviceHost>? plugins = null)
    {
        session ??= new ProjectSession();
        var clock = SystemMonotonicClock.Instance;
        var provider = new LoopbackMidiProvider(clock);
        var port = provider.CreatePort("Synth", "synth");
        var endpoints = new EndpointDirectory([provider]);
        var playback = new PlaybackController(session, endpoints, ProfileCatalog.Empty, clock, startThread: false);
        var window = new MainWindow { Width = 1440, Height = 1600 };
        var viewModel = new MainViewModel(session, playback, endpoints, new NoDialogs(), new AvaloniaDispatcher(), plugins: plugins?.Invoke(session));
        window.Attach(viewModel);
        window.Show();
        await viewModel.InitializeAsync();
        var app = new AppUnderTest(window, viewModel, session, playback, port);
        app.Settle();
        return app;
    }

    /// <summary>Runs pending UI work and lays the window out.</summary>
    public void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    /// <summary>The named control in the window.</summary>
    public T Find<T>(string name)
        where T : Control =>
        Window.FindControl<T>(name) ?? throw new InvalidOperationException($"No control named {name}.");

    /// <summary>The first control of type <typeparamref name="T"/> inside <paramref name="scope"/> that matches.</summary>
    public static T Within<T>(Control scope, Func<T, bool> match)
        where T : Control =>
        scope.GetLogicalDescendants().OfType<T>().FirstOrDefault(match)
        ?? throw new InvalidOperationException($"No matching {typeof(T).Name} in {scope.Name ?? scope.GetType().Name}.");

    /// <summary>Clicks <paramref name="control"/> with the mouse, as a musician would: scrolled into view, then pressed and released.</summary>
    public void Click(Control control)
    {
        control.BringIntoView();
        Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
            ?? throw new InvalidOperationException($"{control} is not in the window.");
        Assert.True(control.IsEffectivelyVisible, $"{control} should be visible to be clicked.");
        Window.MouseDown(centre, MouseButton.Left);
        Window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    /// <summary>Waits, letting the UI thread run, until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Renders the window and saves the frame as a PNG in the test output, for looking at.</summary>
    public string SaveFrame(string name)
    {
        Window.UpdateLayout();
        using var frame = Window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
        var directory = Path.Combine(AppContext.BaseDirectory, "frames");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Window.Close();
        await ViewModel.DisposeAsync();
    }

    private sealed class NoDialogs : IUserInteraction
    {
        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive) => Task.FromResult(true);

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(UnsavedChangesChoice.Discard);
    }
}
