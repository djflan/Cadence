using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Cadence.Presentation;

namespace Cadence.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private MainViewModel? _viewModel;
    private bool _closeConfirmed;

    public MainWindow() => InitializeComponent();

    public void Attach(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        _frameTimer.Tick += (_, _) => OnFrame();
        _frameTimer.Start();
        _autosaveTimer.Tick += async (_, _) => await viewModel.AutosaveAsync();
        _autosaveTimer.Start();

        Timeline.SeekRequested += (_, tick) => viewModel.SeekTo(tick);
        TimelineScroller.ScrollChanged += (_, _) => SyncTimelineViewport();
        TimelineScroller.SizeChanged += (_, _) => SyncTimelineViewport();
        TrackList.SelectionChanged += (_, _) => Timeline.SelectedIndex = TrackList.SelectedIndex;
        viewModel.MonitorEntries.CollectionChanged += (_, _) =>
        {
            if (viewModel.MonitorEntries.Count > 0)
            {
                MonitorList.ScrollIntoView(viewModel.MonitorEntries.Count - 1);
            }
        };

        AddKeyBindings(viewModel);
        Closing += OnClosing;
    }

    private void OnFrame()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.OnFrame();
        Playhead.X = Timeline.TickToX(_viewModel.PlayheadTick);
    }

    private void SyncTimelineViewport()
    {
        Timeline.VisibleLeft = TimelineScroller.Offset.X;
        Timeline.VisibleWidth = TimelineScroller.Viewport.Width;
    }

    private void AddKeyBindings(MainViewModel vm)
    {
        var command = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        void Bind(Key key, KeyModifiers modifiers, System.Windows.Input.ICommand target) =>
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = target });

        Bind(Key.Space, KeyModifiers.None, vm.PlayPauseCommand);
        Bind(Key.Home, KeyModifiers.None, vm.ReturnToStartCommand);
        Bind(Key.L, KeyModifiers.None, vm.ToggleLoopCommand);
        Bind(Key.OemPeriod, command, vm.PanicCommand);
        Bind(Key.Z, command, vm.UndoCommand);
        Bind(Key.Z, command | KeyModifiers.Shift, vm.RedoCommand);
        Bind(Key.N, command, vm.NewProjectCommand);
        Bind(Key.O, command, vm.OpenCommand);
        Bind(Key.S, command, vm.SaveCommand);
        Bind(Key.S, command | KeyModifiers.Shift, vm.SaveAsCommand);
        Bind(Key.I, command, vm.ImportMidiCommand);
        Bind(Key.E, command, vm.ExportMidiCommand);
        Bind(Key.T, command, vm.AddTrackCommand);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || _viewModel is null)
        {
            return;
        }

        e.Cancel = true;
        if (await _viewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            _frameTimer.Stop();
            _autosaveTimer.Stop();
            Close();
        }
    }
}
