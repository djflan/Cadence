using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        viewModel.SelectedTracks.CollectionChanged += (_, _) => SyncSelectedLanes();
        viewModel.Tracks.CollectionChanged += (_, _) => SyncSelectedLanes();
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        viewModel.MonitorEntries.CollectionChanged += (_, _) =>
        {
            if (viewModel.MonitorEntries.Count > 0)
            {
                MonitorList.ScrollIntoView(viewModel.MonitorEntries.Count - 1);
            }
        };

        TimelineScroller.AddHandler(PointerWheelChangedEvent, OnTimelineWheel, RoutingStrategies.Tunnel);
        TimelineScroller.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, OnTimelinePinch, RoutingStrategies.Tunnel);

        var command = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var nativeMenuBar = OperatingSystem.IsMacOS();
        if (nativeMenuBar)
        {
            AppMenu.Install(this, viewModel, command);
            FileMenuButton.IsVisible = false;
        }

        AddKeyBindings(viewModel, command, skipMenuGestures: nativeMenuBar);
        Closing += OnClosing;

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>The first dropped file Cadence can open, as a local path.</summary>
    private static string? DroppedFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(item => item.TryGetLocalPath())
            .FirstOrDefault(path => path is not null && MainViewModel.CanOpenFile(path));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var path = DroppedFile(e);
        e.DragEffects = path is null ? DragDropEffects.None : DragDropEffects.Copy;
        DropOverlay.IsVisible = path is not null;
        if (path is not null)
        {
            var isProject = path.EndsWith(".cadence", StringComparison.OrdinalIgnoreCase);
            DropText.Text = isProject ? "Drop to open project" : "Drop to import MIDI file";
            DropDetail.Text = System.IO.Path.GetFileName(path);
        }

        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
        if (_viewModel is not null && DroppedFile(e) is { } path)
        {
            Activate();
            await _viewModel.OpenFileAsync(path);
        }
    }

    /// <summary>⌘/Ctrl + scroll zooms around the pointer; so does plain scrolling over the bar ruler.</summary>
    private void OnTimelineWheel(object? sender, PointerWheelEventArgs e)
    {
        var command = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var overRuler = e.GetPosition(Timeline).Y < Controls.TimelineView.RulerHeight;
        if ((!e.KeyModifiers.HasFlag(command) && !overRuler) || e.Delta.Y == 0)
        {
            return;
        }

        ZoomAround(e.GetPosition(TimelineScroller).X, Math.Pow(1.12, e.Delta.Y));
        e.Handled = true;
    }

    private void OnTimelinePinch(object? sender, PointerDeltaEventArgs e)
    {
        ZoomAround(e.GetPosition(TimelineScroller).X, 1 + e.Delta.X);
        e.Handled = true;
    }

    /// <summary>Zooms while keeping the musical position under <paramref name="viewportX"/> in place.</summary>
    private void ZoomAround(double viewportX, double factor)
    {
        if (_viewModel is null || factor <= 0)
        {
            return;
        }

        var tick = Timeline.XToTick(TimelineScroller.Offset.X + viewportX);
        _viewModel.ZoomBy(factor);
        TimelineScroller.UpdateLayout();
        var x = Timeline.TickToX(tick) - viewportX;
        TimelineScroller.Offset = new Vector(Math.Max(0, x), TimelineScroller.Offset.Y);
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

    private void AddKeyBindings(MainViewModel vm, KeyModifiers command, bool skipMenuGestures)
    {
        void Bind(Key key, KeyModifiers modifiers, System.Windows.Input.ICommand target)
        {
            var isMenuGesture = modifiers.HasFlag(command) && AppMenu.MenuGestures.Contains((key, modifiers & ~command));
            if (!(skipMenuGestures && isMenuGesture))
            {
                KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = target });
            }
        }

        Bind(Key.D, command, vm.DuplicateTracksCommand);
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
        Bind(Key.OemPlus, command, vm.ZoomInCommand);
        Bind(Key.OemMinus, command, vm.ZoomOutCommand);
    }

    private void SyncSelectedLanes()
    {
        if (_viewModel is { } vm)
        {
            Timeline.SelectedLanes = vm.SelectedTracks.Select(t => vm.Tracks.IndexOf(t)).Where(i => i >= 0).ToHashSet();
        }
    }

    /// <summary>The text field being edited, if any. Single-key shortcuts stand aside while one is focused.</summary>
    internal TextBox? FocusedTextBox() => FocusManager?.GetFocusedElement() as TextBox;

    /// <summary>Select All: the text in a focused field, otherwise every track.</summary>
    internal void SelectAll()
    {
        if (FocusedTextBox() is { } text)
        {
            text.SelectAll();
        }
        else
        {
            _viewModel?.SelectAllTracksCommand.Execute(null);
        }
    }

    /// <summary>Starts editing the first selected track's name.</summary>
    internal void BeginRename()
    {
        if (_viewModel?.SelectedTrack is not { } track
            || TrackList.ContainerFromItem(track) is not { } container
            || container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is not { } name)
        {
            return;
        }

        // Deferred so the key that started the rename cannot also type into the field.
        Dispatcher.UIThread.Post(() =>
        {
            name.Focus();
            name.SelectAll();
        }, DispatcherPriority.Background);
    }

    /// <summary>Leaves a text field by focusing its track row, so the next keys act on tracks again.</summary>
    private void EndEditing(TextBox editing)
    {
        var row = editing.DataContext is TrackViewModel track ? TrackList.ContainerFromItem(track) : null;
        if (row?.Focus() != true)
        {
            Timeline.Focus();
        }
    }

    internal void ShowShortcuts() => _ = Shortcuts.CreateWindow().ShowDialog(this);

    /// <summary>
    /// Single-key and navigation shortcuts. Runs before focused controls (tunnel) so that, for
    /// example, Space plays even when the track list has focus, but never while text is being edited.
    /// </summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (FocusedTextBox() is { } editing)
        {
            // Return commits a track name and Escape cancels it; everything else belongs to the field.
            if (editing.Classes.Contains("inline") && e.Key is Key.Return or Key.Enter or Key.Escape)
            {
                if (editing.DataContext is TrackViewModel track)
                {
                    if (e.Key == Key.Escape)
                    {
                        editing.Text = track.Name;
                    }
                    else if (!string.IsNullOrWhiteSpace(editing.Text))
                    {
                        track.Name = editing.Text.Trim();
                    }
                }

                EndEditing(editing);
                e.Handled = true;
            }

            return;
        }

        var command = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var modifiers = e.KeyModifiers;
        System.Windows.Input.ICommand? target = (e.Key, modifiers) switch
        {
            (Key.Space, KeyModifiers.None) => vm.PlayPauseCommand,
            (Key.Home, KeyModifiers.None) => vm.ReturnToStartCommand,
            (Key.End, KeyModifiers.None) => vm.GoToEndCommand,
            (Key.OemComma, KeyModifiers.None) => vm.PreviousBarCommand,
            (Key.OemPeriod, KeyModifiers.None) => vm.NextBarCommand,
            (Key.L, KeyModifiers.None) => vm.ToggleLoopCommand,
            (Key.M, KeyModifiers.None) => vm.ToggleMuteCommand,
            (Key.S, KeyModifiers.None) => vm.ToggleSoloCommand,
            (Key.Delete or Key.Back, KeyModifiers.None) => vm.DeleteTracksCommand,
            _ => null,
        };

        if (target is not null)
        {
            target.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Return or Key.Enter when modifiers == KeyModifiers.None:
                BeginRename();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when (modifiers is KeyModifiers.None or KeyModifiers.Shift) && !TrackList.IsKeyboardFocusWithin:
                vm.SelectAdjacent(e.Key == Key.Up ? -1 : 1, extend: modifiers == KeyModifiers.Shift);
                e.Handled = true;
                break;
            case Key.A when modifiers == command:
                SelectAll();
                e.Handled = true;
                break;
            case Key.OemQuestion when modifiers.HasFlag(command):
                ShowShortcuts();
                e.Handled = true;
                break;
        }
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
