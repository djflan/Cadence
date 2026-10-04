using System.Collections.ObjectModel;
using System.Globalization;
using Cadence.Application.Editing;
using Cadence.Application.Sessions;
using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Infrastructure.Projects;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Files;
using Cadence.Playback;
using Cadence.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadence.Presentation;

public enum MessageSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A line in the messages panel. Severity is always also spelled out in text.</summary>
public sealed record MessageItem(MessageSeverity Severity, string Source, string Text)
{
    public string SeverityText => Severity.ToString();
}

/// <summary>
/// The main window's state and commands. A thin client of <see cref="ProjectSession"/> and
/// <see cref="PlaybackController"/>: it owns no musical data and never schedules MIDI.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int MaxMessages = 500;
    private const int MaxMonitorEntries = 400;

    private readonly ProjectSession _session;
    private readonly PlaybackController _playback;
    private readonly EndpointDirectory _endpoints;
    private readonly IUserInteraction _ui;
    private readonly IUiDispatcher _dispatcher;
    private readonly MidiMonitor? _monitor;
    private IReadOnlyList<EndpointDescriptor> _outputDescriptors = [];
    private int _refreshing;

    public MainViewModel(ProjectSession session, PlaybackController playback, EndpointDirectory endpoints, IUserInteraction ui, IUiDispatcher dispatcher, MidiMonitor? monitor = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _monitor = monitor;
        Project = session.Project;
        Selection = new SelectionViewModel(this);
        SelectedTracks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SelectedTrack));
            SyncSelection();
        };

        // Session work may finish on a thread-pool thread; view state is only touched on the UI thread.
        _session.Changed += (_, _) => _dispatcher.Run(OnSessionChanged);
        _endpoints.EndpointsChanged += (_, _) => _dispatcher.Post(() => _ = RefreshAsync());
        foreach (var failure in _playback.Profiles.Failures)
        {
            AddMessage(MessageSeverity.Warning, "Profiles", $"{Path.GetFileName(failure.Source)} was not loaded: {(failure.Diagnostics.Count > 0 ? failure.Diagnostics[0].ToString() : "unknown error")}");
        }
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    public ObservableCollection<OutputOption> Outputs { get; } = [];

    public ObservableCollection<MessageItem> Messages { get; } = [];

    public ObservableCollection<MonitorEntry> MonitorEntries { get; } = [];

    public string? MonitorName => _monitor?.EndpointName;

    [ObservableProperty]
    public partial Project Project { get; private set; }

    /// <summary>
    /// The selected tracks, in selection order. The list view binds to this collection directly, so
    /// changes made here (select all, add track) are reflected in the UI and vice versa.
    /// </summary>
    public ObservableCollection<TrackViewModel> SelectedTracks { get; } = [];

    /// <summary>The inspector for <see cref="SelectedTracks"/>.</summary>
    public SelectionViewModel Selection { get; }

    /// <summary>The first selected track, or null.</summary>
    public TrackViewModel? SelectedTrack => SelectedTracks.Count > 0 ? SelectedTracks[0] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string ProjectName { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool IsDirty { get; private set; }

    public string WindowTitle => $"{ProjectName}{(IsDirty ? " — edited" : string.Empty)} · Cadence";

    [ObservableProperty]
    public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    public partial long PlayheadTick { get; private set; }

    [ObservableProperty]
    public partial string PositionText { get; private set; } = "1.1.000";

    [ObservableProperty]
    public partial string TimeText { get; private set; } = "0:00.000";

    [ObservableProperty]
    public partial string TempoText { get; private set; } = "120.0";

    [ObservableProperty]
    public partial string MeterText { get; private set; } = "4/4";

    [ObservableProperty]
    public partial bool IsLoopEnabled { get; private set; }

    [ObservableProperty]
    public partial string TimingText { get; private set; } = "Idle";

    [ObservableProperty]
    public partial string OutputsText { get; private set; } = string.Empty;

    public string PlayButtonText => IsPlaying ? "Pause" : "Play";

    public const double MinZoom = 6;
    public const double MaxZoom = 160;
    public const double ZoomStep = 1.25;

    /// <summary>Timeline scale in pixels per quarter note, shared by the slider, menu, keys, and gestures.</summary>
    [ObservableProperty]
    public partial double Zoom { get; set; } = 40;

    partial void OnZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, MinZoom, MaxZoom);
        if (clamped != value)
        {
            Zoom = clamped;
        }
    }

    /// <summary>Multiplies the zoom by <paramref name="factor"/>, staying within range.</summary>
    public void ZoomBy(double factor) => Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomIn() => ZoomBy(ZoomStep);

    [RelayCommand]
    private void ZoomOut() => ZoomBy(1 / ZoomStep);

    /// <summary>Loads routes and outputs for the initial project.</summary>
    public Task InitializeAsync() => SyncAndRefreshAsync();

    internal void Execute(IProjectCommand command) => _session.Execute(command);

    internal EndpointDescriptor? FindEndpoint(EndpointId id) => _outputDescriptors.FirstOrDefault(e => e.Id == id);

    internal DeviceProfile? FindProfile(string id) => _playback.Profiles.Find(id);

    /// <summary>Applies a routing change to every selected track as one undoable step.</summary>
    internal void ApplyToSelection(string label, Func<Domain.Routing.TrackRoute, Domain.Routing.TrackRoute> change)
    {
        var tracks = SelectedTracks.ToList();
        if (tracks.Count == 0)
        {
            return;
        }

        var commands = tracks.Select(t => ProjectCommands.SetRoute(change(Project.Routing.Find(t.Id) ?? new Domain.Routing.TrackRoute(t.Id))));
        Execute(ProjectCommands.Batch(tracks.Count == 1 ? label : $"{label} ({tracks.Count} Tracks)", commands));
    }

    /// <summary>Makes <paramref name="track"/> the only selected track.</summary>
    public void Select(TrackViewModel track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (SelectedTracks.Count == 1 && SelectedTracks[0] == track)
        {
            return;
        }

        SelectedTracks.Clear();
        SelectedTracks.Add(track);
    }

    /// <summary>Moves the selection up or down; with <paramref name="extend"/>, adds the next track instead.</summary>
    public void SelectAdjacent(int delta, bool extend)
    {
        if (Tracks.Count == 0)
        {
            return;
        }

        var anchor = SelectedTracks.Count > 0 ? SelectedTracks[^1] : null;
        var index = anchor is null ? (delta > 0 ? 0 : Tracks.Count - 1) : Math.Clamp(Tracks.IndexOf(anchor) + delta, 0, Tracks.Count - 1);
        var target = Tracks[index];
        if (extend)
        {
            if (!SelectedTracks.Contains(target))
            {
                SelectedTracks.Add(target);
            }
        }
        else
        {
            Select(target);
        }
    }

    /// <summary>Polls engine state for display. Called by a UI timer (about 30 Hz); never schedules MIDI.</summary>
    public void OnFrame()
    {
        var engine = _playback.Engine;
        var sequence = Project.Sequence;
        var tick = engine.Position;
        IsPlaying = engine.State == TransportState.Playing;
        OnPropertyChanged(nameof(PlayButtonText));
        PlayheadTick = tick.Value;
        PositionText = Formatting.Position(sequence.MeterMap.ToBarBeatTick(tick));
        TimeText = Formatting.Time(sequence.TempoMap.TimeAt(tick));
        TempoText = Formatting.Tempo(sequence.TempoMap.TempoAt(tick));
        MeterText = sequence.MeterMap.SignatureAt(tick).ToString();

        var stats = engine.Statistics.Snapshot();
        TimingText = stats.Dispatched == 0
            ? "No messages sent yet"
            : string.Create(CultureInfo.InvariantCulture, $"{stats.Dispatched} sent · p95 ≤ {Formatting.Milliseconds(stats.Percentile(95))} · {stats.Late} late · {stats.Dropped} dropped");

        if (_monitor is not null)
        {
            foreach (var entry in _monitor.Drain())
            {
                MonitorEntries.Add(entry);
            }

            while (MonitorEntries.Count > MaxMonitorEntries)
            {
                MonitorEntries.RemoveAt(0);
            }
        }
    }

    public Task AutosaveAsync() => _session.AutosaveAsync();

    /// <summary>Asks about unsaved changes before the window closes. Returns false to keep it open.</summary>
    public async Task<bool> ConfirmCloseAsync() => await ResolveUnsavedChangesAsync();

    public void SeekTo(long tick)
    {
        var target = new Tick(Math.Max(0, tick));
        if (IsPlaying)
        {
            _playback.Seek(target);
        }
        else
        {
            _playback.Engine.Seek(target);
        }

        PlayheadTick = target.Value;
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (await ResolveUnsavedChangesAsync())
        {
            _playback.Stop();
            _session.New();
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (await ResolveUnsavedChangesAsync() && await _ui.PickOpenFileAsync("Open Project", [FileFilters.Project]) is { } path)
        {
            await OpenProjectAsync(path);
        }
    }

    /// <summary>True for files Cadence can open by dropping them on the window: projects and MIDI files.</summary>
    public static bool CanOpenFile(string path) => IsProject(path) || IsMidi(path);

    /// <summary>
    /// Opens a dropped project, or imports a dropped MIDI file, after resolving unsaved changes.
    /// </summary>
    public async Task OpenFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!CanOpenFile(path))
        {
            AddMessage(MessageSeverity.Warning, "Open", $"{Path.GetFileName(path)} is not a Cadence project or MIDI file.");
            return;
        }

        if (!await ResolveUnsavedChangesAsync())
        {
            return;
        }

        if (IsProject(path))
        {
            await OpenProjectAsync(path);
        }
        else
        {
            await ImportMidiFileAsync(path);
        }
    }

    private static bool IsProject(string path) => HasExtension(path, FileFilters.Project);

    private static bool IsMidi(string path) => HasExtension(path, FileFilters.Midi);

    private static bool HasExtension(string path, FileFilter filter) =>
        filter.Extensions.Any(e => Path.GetExtension(path).Equals("." + e, StringComparison.OrdinalIgnoreCase));

    private Task OpenProjectAsync(string path) => RunAsync("Open", async () =>
    {
        _playback.Stop();
        var report = await _session.OpenAsync(path);
        if (report.RecoveredFromBackup)
        {
            AddMessage(MessageSeverity.Warning, "Open", $"The project file was damaged ({report.Problem}). Cadence opened the backup copy; save to repair the file.");
        }

        if (report.RecoveryAvailable && await _ui.ConfirmAsync("Restore unsaved work?", "Cadence found autosaved changes newer than this project. Restore them?", "Restore", destructive: false))
        {
            await _session.RestoreRecoveryAsync();
            AddMessage(MessageSeverity.Info, "Open", "Restored autosaved changes. Save to keep them.");
        }
    });

    [RelayCommand]
    private Task SaveAsync() => _session.FilePath is null ? SaveAsAsync() : RunAsync("Save", () => _session.SaveAsync());

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (await _ui.PickSaveFileAsync("Save Project", ProjectName + ".cadence", FileFilters.Project) is { } path)
        {
            await RunAsync("Save", () => _session.SaveAsync(path));
        }
    }

    [RelayCommand]
    private async Task ImportMidiAsync()
    {
        if (await ResolveUnsavedChangesAsync() && await _ui.PickOpenFileAsync("Import MIDI File", [FileFilters.Midi]) is { } path)
        {
            await ImportMidiFileAsync(path);
        }
    }

    private Task ImportMidiFileAsync(string path) => RunAsync("Import", async () =>
    {
        _playback.Stop();
        var report = await _session.ImportMidiAsync(path);
        ReportMidi("Import", report.ImportDiagnostics);
        AddMessage(MessageSeverity.Info, "Import", $"Imported {Path.GetFileName(path)} with {_session.Project.Sequence.Tracks.Length} tracks. Choose an output for each track to hear it.");
    });

    [RelayCommand]
    private async Task ExportMidiAsync()
    {
        if (await _ui.PickSaveFileAsync("Export MIDI File", ProjectName + ".mid", FileFilters.Midi) is { } path)
        {
            await RunAsync("Export", async () =>
            {
                ReportMidi("Export", await _session.ExportMidiAsync(path));
                AddMessage(MessageSeverity.Info, "Export", $"Exported {Path.GetFileName(path)}.");
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _session.History.Undo();

    private bool CanUndo() => _session.History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _session.History.Redo();

    private bool CanRedo() => _session.History.CanRedo;

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (_playback.Engine.State == TransportState.Playing)
        {
            _playback.Stop();
            return;
        }

        await RunAsync("Play", () => _playback.PlayAsync());
    }

    /// <summary>Stops; when already stopped, returns to the start.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (_playback.Engine.State == TransportState.Playing)
        {
            _playback.Stop();
        }
        else
        {
            SeekTo(0);
        }
    }

    [RelayCommand]
    private void ReturnToStart() => SeekTo(0);

    [RelayCommand]
    private void Panic()
    {
        _playback.Panic();
        AddMessage(MessageSeverity.Info, "Panic", "Sent All Notes Off, All Sound Off, and sustain off on every channel of every output.");
    }

    /// <summary>Turns the loop off, or loops four bars starting at the bar containing the playhead.</summary>
    [RelayCommand]
    private void ToggleLoop()
    {
        if (Project.Loop is not null)
        {
            Execute(ProjectCommands.SetLoop(null));
            return;
        }

        var meter = Project.Sequence.MeterMap;
        var start = meter.BarStart(new Tick(PlayheadTick));
        var bar = meter.ToBarBeatTick(start).Bar;
        var end = meter.TryGetTick(new BarBeatTick(bar + 4, 1, 0), out var e) ? e : start + new TickSpan(16L * Project.Sequence.Ppqn.TicksPerQuarterNote);
        Execute(ProjectCommands.SetLoop(new TickRange(start, end)));
    }

    [RelayCommand]
    private void AddTrack()
    {
        var track = Track.Create(string.Create(CultureInfo.InvariantCulture, $"Track {Project.Sequence.Tracks.Length + 1}"));
        Execute(ProjectCommands.AddTrack(track));
        if (Tracks.FirstOrDefault(t => t.Id == track.Id) is { } added)
        {
            Select(added);
        }
    }

    [RelayCommand]
    private void SelectAllTracks()
    {
        foreach (var track in Tracks.Where(t => !SelectedTracks.Contains(t)).ToList())
        {
            SelectedTracks.Add(track);
        }
    }

    [RelayCommand]
    private void DuplicateTracks()
    {
        if (SelectedTracks.Count > 0)
        {
            Execute(ProjectCommands.DuplicateTracks([.. SelectedTracks.Select(t => t.Id)]));
        }
    }

    /// <summary>Mutes the selected tracks, or unmutes them if they are all muted.</summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (SelectedTracks.Count > 0)
        {
            Execute(ProjectCommands.SetMuted([.. SelectedTracks.Select(t => t.Id)], !SelectedTracks.All(t => t.IsMuted)));
        }
    }

    /// <summary>Solos the selected tracks, or unsolos them if they are all soloed.</summary>
    [RelayCommand]
    private void ToggleSolo()
    {
        if (SelectedTracks.Count > 0)
        {
            Execute(ProjectCommands.SetSoloed([.. SelectedTracks.Select(t => t.Id)], !SelectedTracks.All(t => t.IsSoloed)));
        }
    }

    [RelayCommand]
    private void PreviousBar() => MoveByBars(-1);

    [RelayCommand]
    private void NextBar() => MoveByBars(1);

    [RelayCommand]
    private void GoToEnd() => SeekTo(Project.Sequence.EndPosition.Value);

    /// <summary>Moves to the start of an adjacent bar. Going back from mid-bar first returns to the bar's start.</summary>
    private void MoveByBars(int bars)
    {
        var meter = Project.Sequence.MeterMap;
        var position = new Tick(PlayheadTick);
        var start = meter.BarStart(position);
        var bar = meter.ToBarBeatTick(start).Bar;
        var targetBar = bars < 0 && start < position ? bar : Math.Max(1, bar + bars);
        if (meter.TryGetTick(new BarBeatTick(targetBar, 1, 0), out var target))
        {
            SeekTo(target.Value);
        }
    }

    [RelayCommand]
    private async Task DeleteTracksAsync()
    {
        var tracks = SelectedTracks.Select(t => Project.Sequence.FindTrack(t.Id)).OfType<Track>().ToList();
        if (tracks.Count == 0)
        {
            return;
        }

        var events = tracks.Sum(t => t.Events.Length);
        var subject = tracks.Count == 1 ? $"\"{tracks[0].Name}\"" : $"{tracks.Count} tracks";
        if (events > 0 && !await _ui.ConfirmAsync(
                tracks.Count == 1 ? "Delete track?" : "Delete tracks?",
                $"{subject} {(tracks.Count == 1 ? "has" : "have")} {events} events. You can undo this.",
                "Delete",
                destructive: true))
        {
            return;
        }

        Execute(ProjectCommands.RemoveTracks([.. tracks.Select(t => t.Id)]));
    }

    /// <summary>Routes every track to the output shown in the inspector.</summary>
    [RelayCommand]
    private void UseOutputForAllTracks()
    {
        if (Selection.Output is not { Id: { } id, IsAvailable: true, IsMixed: false } || FindEndpoint(id) is not { } endpoint)
        {
            return;
        }

        var reference = Application.Routing.RouteResolver.ReferenceTo(endpoint);
        foreach (var track in Project.Sequence.Tracks)
        {
            var route = Project.Routing.Find(track.Id) ?? new Domain.Routing.TrackRoute(track.Id);
            Execute(ProjectCommands.SetRoute(route with { Endpoint = reference }));
        }
    }

    /// <summary>Sends the selected track's profile setup messages, asking first for anything that resets the instrument.</summary>
    [RelayCommand]
    private async Task InitializeInstrumentAsync()
    {
        if (SelectedTracks.Count != 1 || SelectedTrack is not { } track)
        {
            return;
        }

        await RunAsync("Initialize", async () =>
        {
            var sent = await _playback.InitializeInstrumentAsync(track.Id, template => _ui.ConfirmAsync(
                $"Send {template.Name}?",
                $"{template.Description ?? template.Name}\n\nThis resets settings on the instrument connected to \"{track.Output?.Name}\".",
                "Send",
                destructive: true));
            AddMessage(MessageSeverity.Info, "Initialize", string.Create(CultureInfo.InvariantCulture, $"Sent {sent} setup message{(sent == 1 ? string.Empty : "s")} to {track.Output?.Name}."));
        });
    }

    [RelayCommand]
    private void ClearMessages() => Messages.Clear();

    private void OnSessionChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        _ = SyncAndRefreshAsync();
    }

    private async Task SyncAndRefreshAsync()
    {
        Project = _session.Project;
        ProjectName = Project.Name;
        IsDirty = _session.IsDirty;
        IsLoopEnabled = Project.Loop is not null;
        SyncTracks();
        await RefreshAsync();
    }

    /// <summary>Re-resolves routes and outputs, then updates every track's routing display.</summary>
    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            // A refresh is running; it will pick up the latest state when the next change arrives.
            _dispatcher.Post(() => _ = RefreshAsync());
            return;
        }

        try
        {
            await _playback.RefreshAsync();
            foreach (var problem in _playback.OutputProblems)
            {
                AddMessage(MessageSeverity.Warning, "Outputs", problem);
            }
        }
        catch (EndpointUnavailableException ex)
        {
            AddMessage(MessageSeverity.Warning, "Outputs", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }

        SyncOutputs();
        SyncTracks();
    }

    private void SyncOutputs()
    {
        _outputDescriptors = _endpoints.GetEndpoints(EndpointDirection.Output);
        Outputs.Clear();
        foreach (var endpoint in _outputDescriptors)
        {
            Outputs.Add(new OutputOption(endpoint.Id, endpoint.DisplayName, Describe(endpoint.Transport), true));
        }

        OutputsText = string.Create(CultureInfo.InvariantCulture, $"{Outputs.Count} output{(Outputs.Count == 1 ? string.Empty : "s")} available");
    }

    private void SyncSelection() => Selection.Sync([.. SelectedTracks], [.. Outputs], _playback.Profiles);

    private static string Describe(EndpointTransport transport) => transport switch
    {
        EndpointTransport.Physical => "Hardware",
        EndpointTransport.Virtual => "Virtual port",
        EndpointTransport.Network => "Network",
        EndpointTransport.Software => "Software instrument",
        EndpointTransport.Test => "Built-in",
        _ => "MIDI output",
    };

    private void SyncTracks()
    {
        var tracks = Project.Sequence.Tracks;
        var selectedIds = SelectedTracks.Select(t => t.Id).ToHashSet();
        for (var i = Tracks.Count - 1; i >= 0; i--)
        {
            if (!tracks.Any(t => t.Id == Tracks[i].Id))
            {
                Tracks.RemoveAt(i);
            }
        }

        var outputs = Outputs.ToList();
        for (var i = 0; i < tracks.Length; i++)
        {
            var existing = Tracks.FirstOrDefault(t => t.Id == tracks[i].Id);
            if (existing is null)
            {
                existing = new TrackViewModel(this, tracks[i].Id);
                Tracks.Insert(Math.Min(i, Tracks.Count), existing);
            }
            else if (Tracks.IndexOf(existing) != i)
            {
                Tracks.Move(Tracks.IndexOf(existing), i);
            }

            existing.Sync(i + 1, tracks[i], _playback.Routes.FirstOrDefault(r => r.Track == tracks[i].Id), outputs);
        }

        for (var i = SelectedTracks.Count - 1; i >= 0; i--)
        {
            if (!Tracks.Contains(SelectedTracks[i]))
            {
                SelectedTracks.RemoveAt(i);
            }
        }

        if (SelectedTracks.Count == 0 && Tracks.Count > 0)
        {
            SelectedTracks.Add(Tracks[0]);
        }
        else if (selectedIds.Count > 0)
        {
            // Rows were updated in place; refresh the inspector from their new values.
            SyncSelection();
        }
    }

    private async Task<bool> ResolveUnsavedChangesAsync()
    {
        if (!_session.IsDirty || _session.Project.Sequence.Tracks.IsEmpty && _session.FilePath is null)
        {
            return true;
        }

        switch (await _ui.AskToSaveChangesAsync(ProjectName))
        {
            case UnsavedChangesChoice.Discard:
                return true;
            case UnsavedChangesChoice.Save:
                await SaveAsync();
                return !_session.IsDirty;
            default:
                return false;
        }
    }

    private async Task RunAsync(string source, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFormatException or SmfFormatException or EndpointUnavailableException or InvalidOperationException)
        {
            AddMessage(MessageSeverity.Error, source, ex.Message);
        }
    }

    private void ReportMidi(string source, IEnumerable<SmfDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            AddMessage(d.Severity == SmfDiagnosticSeverity.Warning ? MessageSeverity.Warning : MessageSeverity.Info, source, d.ToString());
        }
    }

    private void AddMessage(MessageSeverity severity, string source, string text)
    {
        Messages.Add(new MessageItem(severity, source, text));
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _monitor?.Dispose();
        await _playback.DisposeAsync();
    }
}
