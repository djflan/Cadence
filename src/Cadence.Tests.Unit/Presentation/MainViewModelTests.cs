using Cadence.Application.Sessions;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Files;
using Cadence.Midi.Timing;
using Cadence.Presentation;
using Cadence.Profiles;

namespace Cadence.Tests.Unit.Presentation;

public sealed class MainViewModelTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cadence-vm-").FullName;
    private readonly VirtualClock _clock = new(TimeSpan.FromSeconds(5));
    private readonly FakeUi _ui = new();
    private readonly LoopbackMidiProvider _provider;
    private readonly EndpointDirectory _directoryOfEndpoints;
    private readonly ProjectSession _session = new();
    private readonly MainViewModel _vm;
    private readonly LoopbackPort _synth;

    public MainViewModelTests()
    {
        _provider = new LoopbackMidiProvider(_clock);
        _synth = _provider.CreatePort("Synth", "synth");
        _directoryOfEndpoints = new EndpointDirectory([_provider]);
        var catalog = ProfileCatalog.Empty.Add("gm", ProfileLoader.Load("""
            { "format": "cadence-device-profile", "schemaVersion": 1, "id": "test.gm", "version": "1", "name": "Test GM",
              "notices": ["Not affiliated."],
              "provenance": { "sources": ["t"], "contributors": ["t"], "license": "MIT", "redistributionConfirmed": true, "verification": "unverified" },
              "banks": [ { "id": "main", "name": "Main", "msb": 0, "lsb": 0, "programs": [ { "number": 1, "name": "Piano" } ] } ],
              "sysex": [ { "id": "reset", "name": "Reset", "effect": "reset", "bytes": "F0 7E 7F 09 01 F7" } ],
              "initialization": [ { "sysex": "reset" } ] }
            """u8));
        var playback = new PlaybackController(_session, _directoryOfEndpoints, catalog, _clock, startThread: false);
        _vm = new MainViewModel(_session, playback, _directoryOfEndpoints, _ui, new ImmediateDispatcher());
    }

    public async ValueTask InitializeAsync() => await _vm.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        _directoryOfEndpoints.Dispose();
        _provider.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> WriteDemoMidiAsync()
    {
        var track = Track.Create("Piano").Add(new NoteEvent(Tick.Zero, new TickSpan(480), MidiChannel.FromIndex(0), NoteNumber.MiddleC, Velocity.Max));
        var path = Path.Combine(_directory, "demo.mid");
        await File.WriteAllBytesAsync(path, SmfWriter.Write(SmfExporter.Export(Sequence.CreateEmpty(new Ppqn(480)).WithTrack(track)).File), TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<TrackViewModel> ImportAsync()
    {
        _ui.NextOpenPath = await WriteDemoMidiAsync();
        await _vm.ImportMidiCommand.ExecuteAsync(null);
        await Settle();
        return Assert.Single(_vm.Tracks);
    }

    private static Task Settle() => Task.Delay(20, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ImportMidi_ShowsTracksAsOfflineUntilRouted()
    {
        var track = await ImportAsync();

        Assert.Equal("demo", _vm.ProjectName);
        Assert.Equal("Piano", track.Name);
        Assert.True(track.IsOffline);
        Assert.Equal("Offline", track.HealthText);
        Assert.Contains(_vm.Messages, m => m.Text.Contains("Choose an output", StringComparison.Ordinal));
        Assert.Same(track, _vm.SelectedTrack);
    }

    [Fact]
    public async Task ChoosingAnOutput_RoutesTheTrackAndIsUndoable()
    {
        var track = await ImportAsync();

        track.Output = track.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();

        Assert.True(track.IsReady);
        Assert.Equal("No profile → Synth", track.RouteText);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.True(_vm.Tracks.Single().IsOffline);
    }

    [Fact]
    public async Task DisconnectedOutput_StaysVisibleAsAPlaceholder()
    {
        var track = await ImportAsync();
        track.Output = track.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();

        _provider.RemovePort("synth");
        await Settle();

        var current = _vm.Tracks.Single();
        Assert.True(current.IsOffline);
        Assert.False(current.Output!.IsAvailable);
        Assert.Equal("Synth", current.Output.Name);
        Assert.Equal("Disconnected", current.Output.Detail);
    }

    [Fact]
    public async Task ChoosingAProfile_OffersItsVoicesAndNotices()
    {
        var track = await ImportAsync();

        track.Profile = track.ProfileChoices.Single(p => p.Name == "Test GM");
        await Settle();
        track = _vm.Tracks.Single();
        track.VoiceBank = track.Banks.Single(b => b.Name == "Main");
        await Settle();
        track = _vm.Tracks.Single();

        Assert.Equal(["Not affiliated."], track.ProfileNotices);
        Assert.Equal(128, track.Programs.Count);
        Assert.Equal("1 · Piano", track.Programs[0].Name);
        Assert.Equal("Program 2", track.Programs[1].Name);
        Assert.Equal(new ProgramNumber(0), track.VoiceProgram!.Program);
    }

    [Fact]
    public async Task MuteAndRename_GoThroughUndoableCommands()
    {
        var track = await ImportAsync();

        track.IsMuted = true;
        track.Name = "Grand";
        await Settle();

        Assert.True(_session.Project.Sequence.Tracks.Single().IsMuted);
        Assert.Equal("Grand", _session.Project.Sequence.Tracks.Single().Name);
        _vm.UndoCommand.Execute(null);
        await Settle();
        Assert.Equal("Piano", _vm.Tracks.Single().Name);
    }

    [Fact]
    public async Task InitializeInstrument_AsksBeforeResetting()
    {
        var track = await ImportAsync();
        track.Output = track.OutputChoices.Single(o => o.Name == "Synth");
        await Settle();
        _vm.Tracks.Single().Profile = _vm.Tracks.Single().ProfileChoices.Single(p => p.Name == "Test GM");
        await Settle();

        _ui.ConfirmAnswer = false;
        await _vm.InitializeInstrumentCommand.ExecuteAsync(null);
        Assert.Empty(_synth.Sent);
        Assert.Contains("Reset", _ui.LastConfirmTitle, StringComparison.Ordinal);

        _ui.ConfirmAnswer = true;
        await _vm.InitializeInstrumentCommand.ExecuteAsync(null);
        Assert.Single(_synth.Sent);
    }

    [Fact]
    public async Task ToggleLoop_LoopsFourBarsThenClears()
    {
        await ImportAsync();

        _vm.ToggleLoopCommand.Execute(null);
        await Settle();
        Assert.True(_vm.IsLoopEnabled);
        Assert.Equal(new Tick(4 * 4 * 480), _session.Project.Loop!.End);

        _vm.ToggleLoopCommand.Execute(null);
        await Settle();
        Assert.False(_vm.IsLoopEnabled);
    }

    [Fact]
    public async Task DeletingATrackWithEvents_RequiresConfirmation()
    {
        await ImportAsync();

        _ui.ConfirmAnswer = false;
        await _vm.DeleteTrackCommand.ExecuteAsync(null);
        Assert.Single(_vm.Tracks);

        _ui.ConfirmAnswer = true;
        await _vm.DeleteTrackCommand.ExecuteAsync(null);
        await Settle();
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public async Task UnsavedChanges_CancelKeepsTheProject()
    {
        await ImportAsync();
        _ui.SaveChoice = UnsavedChangesChoice.Cancel;

        await _vm.NewProjectCommand.ExecuteAsync(null);

        Assert.Single(_vm.Tracks);
        Assert.False(await _vm.ConfirmCloseAsync());

        _ui.SaveChoice = UnsavedChangesChoice.Discard;
        await _vm.NewProjectCommand.ExecuteAsync(null);
        await Settle();
        Assert.Empty(_vm.Tracks);
    }

    [Fact]
    public async Task SaveAs_WritesTheProjectAndClearsDirty()
    {
        await ImportAsync();
        _ui.NextSavePath = Path.Combine(_directory, "song.cadence");

        await _vm.SaveCommand.ExecuteAsync(null);
        await Settle();

        Assert.True(File.Exists(_ui.NextSavePath));
        Assert.False(_vm.IsDirty);
        Assert.Equal("demo · Cadence", _vm.WindowTitle);
    }

    [Fact]
    public async Task OnFrame_FormatsThePlayhead()
    {
        await ImportAsync();
        _vm.SeekTo(480 * 5);
        _vm.Tracks.Single().Output = _vm.Tracks.Single().OutputChoices.Single(o => o.Name == "Synth");

        _vm.OnFrame();

        Assert.Equal("2.2.000", _vm.PositionText);
        Assert.Equal("0:02.500", _vm.TimeText);
        Assert.Equal("120.0", _vm.TempoText);
        Assert.Equal("4/4", _vm.MeterText);
    }

    [Theory]
    [InlineData("song.cadence", true)]
    [InlineData("SONG.MID", true)]
    [InlineData("tune.midi", true)]
    [InlineData("notes.txt", false)]
    [InlineData("noextension", false)]
    public void CanOpenFile_AcceptsProjectsAndMidiFiles(string path, bool expected) =>
        Assert.Equal(expected, MainViewModel.CanOpenFile(path));

    [Fact]
    public async Task DroppedMidiFile_IsImported()
    {
        await _vm.OpenFileAsync(await WriteDemoMidiAsync());
        await Settle();

        Assert.Equal("Piano", Assert.Single(_vm.Tracks).Name);
    }

    [Fact]
    public async Task DroppedProject_IsOpenedAfterAskingAboutUnsavedChanges()
    {
        await ImportAsync();
        var path = Path.Combine(_directory, "dropped.cadence");
        await new ProjectSession().SaveAsync(path, TestContext.Current.CancellationToken);

        _ui.SaveChoice = UnsavedChangesChoice.Cancel;
        await _vm.OpenFileAsync(path);
        Assert.Single(_vm.Tracks);

        _ui.SaveChoice = UnsavedChangesChoice.Discard;
        await _vm.OpenFileAsync(path);
        await Settle();
        Assert.Empty(_vm.Tracks);
        Assert.Equal("Untitled", _vm.ProjectName);
    }

    [Fact]
    public async Task DroppedUnsupportedFile_IsExplained()
    {
        await _vm.OpenFileAsync(Path.Combine(_directory, "readme.txt"));

        Assert.Contains(_vm.Messages, m => m.Severity == MessageSeverity.Warning && m.Text.Contains("not a Cadence project or MIDI file", StringComparison.Ordinal));
    }

    [Fact]
    public void Zoom_StepsAndStaysInRange()
    {
        _vm.ZoomInCommand.Execute(null);
        Assert.Equal(50, _vm.Zoom, precision: 6);
        _vm.ZoomOutCommand.Execute(null);
        Assert.Equal(40, _vm.Zoom, precision: 6);

        _vm.ZoomBy(1000);
        Assert.Equal(MainViewModel.MaxZoom, _vm.Zoom);
        _vm.Zoom = 0;
        Assert.Equal(MainViewModel.MinZoom, _vm.Zoom);
    }

    [Fact]
    public async Task Errors_AreReportedAsMessagesNotThrown()
    {
        var bad = Path.Combine(_directory, "broken.mid");
        await File.WriteAllTextAsync(bad, "not midi", TestContext.Current.CancellationToken);
        _ui.NextOpenPath = bad;

        await _vm.ImportMidiCommand.ExecuteAsync(null);

        Assert.Contains(_vm.Messages, m => m.Severity == MessageSeverity.Error && m.Source == "Import");
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();
    }

    private sealed class FakeUi : IUserInteraction
    {
        public string? NextOpenPath { get; set; }

        public string? NextSavePath { get; set; }

        public bool ConfirmAnswer { get; set; } = true;

        public string? LastConfirmTitle { get; private set; }

        public UnsavedChangesChoice SaveChoice { get; set; } = UnsavedChangesChoice.Discard;

        public Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult(NextOpenPath);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, FileFilter filter) => Task.FromResult(NextSavePath);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive)
        {
            LastConfirmTitle = title;
            return Task.FromResult(ConfirmAnswer);
        }

        public Task<UnsavedChangesChoice> AskToSaveChangesAsync(string projectName) => Task.FromResult(SaveChoice);
    }
}
