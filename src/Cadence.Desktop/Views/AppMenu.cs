using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Cadence.Presentation;

namespace Cadence.Desktop.Views;

/// <summary>
/// The menu bar, following macOS conventions: an application menu, then File, Edit, Track,
/// Transport, View, Window, and Help. On macOS it appears in the system menu bar; elsewhere the
/// in-window controls and key bindings provide the same commands.
/// </summary>
internal static class AppMenu
{
    public const string HelpUrl = "https://github.com/djflan/Cadence#readme";

    /// <summary>Gestures handled by the menu bar, so the window must not bind them again.</summary>
    public static readonly (Key Key, KeyModifiers Extra)[] MenuGestures =
    [
        (Key.N, KeyModifiers.None), (Key.O, KeyModifiers.None), (Key.W, KeyModifiers.None), (Key.S, KeyModifiers.None),
        (Key.S, KeyModifiers.Shift), (Key.I, KeyModifiers.None), (Key.E, KeyModifiers.None), (Key.Z, KeyModifiers.None),
        (Key.Z, KeyModifiers.Shift), (Key.T, KeyModifiers.None), (Key.OemPeriod, KeyModifiers.None), (Key.OemPlus, KeyModifiers.None),
        (Key.OemMinus, KeyModifiers.None), (Key.M, KeyModifiers.None),
    ];

    public static void Install(MainWindow window, MainViewModel vm, KeyModifiers command)
    {
        KeyGesture Cmd(Key key, KeyModifiers extra = KeyModifiers.None) => new(key, command | extra);

        // Items added to the application menu; macOS supplies Services, Hide, Hide Others, Show All, and Quit.
        var about = Item("About Cadence", () => _ = new AboutWindow().ShowDialog(window));
        if (Avalonia.Application.Current is { } app)
        {
            NativeMenu.SetMenu(app, [about]);
        }

        NativeMenu.SetMenu(window,
        [
            Submenu("File",
                Item("New Project", vm.NewProjectCommand, Cmd(Key.N)),
                Item("Open…", vm.OpenCommand, Cmd(Key.O)),
                new NativeMenuItemSeparator(),
                Item("Close Window", window.Close, Cmd(Key.W)),
                Item("Save", vm.SaveCommand, Cmd(Key.S)),
                Item("Save As…", vm.SaveAsCommand, Cmd(Key.S, KeyModifiers.Shift)),
                new NativeMenuItemSeparator(),
                Item("Import MIDI File…", vm.ImportMidiCommand, Cmd(Key.I)),
                Item("Export MIDI File…", vm.ExportMidiCommand, Cmd(Key.E))),
            Submenu("Edit",
                Item("Undo", vm.UndoCommand, Cmd(Key.Z)),
                Item("Redo", vm.RedoCommand, Cmd(Key.Z, KeyModifiers.Shift))),
            Submenu("Track",
                Item("Add Track", vm.AddTrackCommand, Cmd(Key.T)),
                Item("Delete Track…", vm.DeleteTrackCommand),
                new NativeMenuItemSeparator(),
                Item("Use Output for All Tracks", vm.UseOutputForAllTracksCommand),
                Item("Initialize Instrument…", vm.InitializeInstrumentCommand)),
            // Space, Home, and L stay window key bindings so they never steal keys from text fields.
            Submenu("Transport",
                Item("Play / Pause    Space", vm.PlayPauseCommand),
                Item("Stop", vm.StopCommand),
                Item("Return to Start    Home", vm.ReturnToStartCommand),
                Item("Loop Four Bars    L", vm.ToggleLoopCommand),
                new NativeMenuItemSeparator(),
                Item("Panic", vm.PanicCommand, Cmd(Key.OemPeriod))),
            Submenu("View",
                Item("Zoom In", vm.ZoomInCommand, Cmd(Key.OemPlus)),
                Item("Zoom Out", vm.ZoomOutCommand, Cmd(Key.OemMinus))),
            Submenu("Window",
                Item("Minimize", () => window.WindowState = WindowState.Minimized, Cmd(Key.M)),
                Item("Zoom", () => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized)),
            Submenu("Help",
                Item("Cadence Help", () => _ = window.Launcher.LaunchUriAsync(new Uri(HelpUrl)))),
        ]);
    }

    private static NativeMenuItem Submenu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            menu.Add(item);
        }

        return new NativeMenuItem(header) { Menu = menu };
    }

    private static NativeMenuItem Item(string header, ICommand command, KeyGesture? gesture = null) =>
        new(header) { Command = command, Gesture = gesture };

    private static NativeMenuItem Item(string header, Action action, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header) { Gesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }
}
