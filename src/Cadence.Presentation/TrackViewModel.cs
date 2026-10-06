using System.Globalization;
using Cadence.Application.Editing;
using Cadence.Application.Routing;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Endpoints;
using Cadence.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadence.Presentation;

/// <summary>
/// One row in the track list: name, mute/solo, and routing status. Routing is edited for the whole
/// selection through <see cref="SelectionViewModel"/>.
/// </summary>
public sealed partial class TrackViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _syncing;

    internal TrackViewModel(MainViewModel owner, TrackId id)
    {
        _owner = owner;
        Id = id;
    }

    public TrackId Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    /// <summary>Armed for recording. View state only: arming is not saved or undoable.</summary>
    [ObservableProperty]
    public partial bool IsArmed { get; set; }

    partial void OnIsArmedChanged(bool value)
    {
        if (!_syncing && value != (_owner.ArmedTrack == this))
        {
            _owner.ToggleArm(this);
        }
    }

    /// <summary>One-based position in the track list.</summary>
    [ObservableProperty]
    public partial int Number { get; private set; }

    /// <summary>Index into the theme's track colour palette.</summary>
    public int ColorIndex => (Number - 1) % 8;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>The stored route, or null if the track has never been routed.</summary>
    public TrackRoute? Route { get; private set; }

    /// <summary>The installed profile the route refers to, if any.</summary>
    public DeviceProfile? ResolvedProfile { get; private set; }

    /// <summary>The track's output as a choice: an available endpoint, a disconnected placeholder, or none.</summary>
    public OutputOption Output { get; private set; } = OutputOption.None;

    public ProfileOption Profile { get; private set; } = ProfileOption.None;

    [ObservableProperty]
    public partial RouteHealth Health { get; private set; }

    public bool IsReady => Health == RouteHealth.Ready;

    public bool NeedsAttention => Health == RouteHealth.Attention;

    public bool IsOffline => Health == RouteHealth.Offline;

    /// <summary>A word for the route's health, so status never depends on colour alone.</summary>
    public string HealthText => Health switch
    {
        RouteHealth.Ready => "Ready",
        RouteHealth.Attention => "Check",
        _ => "Offline",
    };

    /// <summary>The output and, when it is not ready, a word for why: "Synth", "Synth · Check", "No output".</summary>
    public string StatusLine => Health switch
    {
        RouteHealth.Ready => Output.Name,
        RouteHealth.Attention => $"{Output.Name} · Check",
        _ when Route?.Endpoint is null => "No output",
        _ => $"{Output.Name} · Offline",
    };

    /// <summary>"Profile → Output", showing the two independent choices side by side.</summary>
    [ObservableProperty]
    public partial string RouteText { get; private set; } = string.Empty;

    public IReadOnlyList<string> Problems { get; private set; } = [];

    public bool CanInitialize => ResolvedProfile is { Initialization.Length: > 0 } && Health != RouteHealth.Offline;

    internal void Sync(int number, Track track, ResolvedRoute? resolved, IReadOnlyList<OutputOption> outputs)
    {
        _syncing = true;
        try
        {
            Number = number;
            Name = track.Name;
            IsMuted = track.IsMuted;
            IsSoloed = track.IsSoloed;
            Summary = Summarize(track);
            Route = resolved?.Route;
            ResolvedProfile = resolved?.Profile.Profile;
            Output = ResolveOutput(outputs, resolved);
            Profile = Route?.Profile is not { } reference
                ? ProfileOption.None
                : ResolvedProfile is { } installed
                    ? new ProfileOption(installed.Id, installed.Name, true)
                    : new ProfileOption(reference.ProfileId, $"{reference.DisplayName ?? reference.ProfileId} (not installed)", false);

            Health = resolved switch
            {
                null or { CanPlay: false } => RouteHealth.Offline,
                { Problems.Length: > 0 } => RouteHealth.Attention,
                _ => RouteHealth.Ready,
            };
            Problems = resolved?.Problems ?? [];
            RouteText = $"{Profile.Name} → {Output.Name}";
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(NeedsAttention));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(ColorIndex));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_syncing && value.Length is > 0 and <= Track.MaxNameLength)
        {
            _owner.Execute(ProjectCommands.RenameTrack(Id, value));
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Execute(ProjectCommands.SetMuted(Id, value));
        }
    }

    partial void OnIsSoloedChanged(bool value)
    {
        if (!_syncing)
        {
            _owner.Execute(ProjectCommands.SetSoloed(Id, value));
        }
    }

    private OutputOption ResolveOutput(IReadOnlyList<OutputOption> outputs, ResolvedRoute? resolved)
    {
        if (Route?.Endpoint is not { } reference)
        {
            return OutputOption.None;
        }

        if (resolved?.Endpoint.Endpoint is { } bound && outputs.FirstOrDefault(o => o.Id == bound.Id) is { } match)
        {
            return match;
        }

        return new OutputOption(new EndpointId(reference.ProviderId, reference.EndpointKey), reference.DisplayName ?? reference.EndpointKey, "Disconnected", false);
    }

    private static string Summarize(Track track)
    {
        var notes = track.Events.OfType<NoteEvent>().ToList();
        var channels = track.Events.OfType<ChannelEvent>().Select(c => c.Channel.Number)
            .Distinct()
            .Order()
            .ToList();
        var channelText = channels.Count switch
        {
            0 => "no channel data",
            1 => string.Create(CultureInfo.InvariantCulture, $"ch {channels[0]}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"ch {string.Join(", ", channels.Take(4))}{(channels.Count > 4 ? "…" : string.Empty)}"),
        };
        return string.Create(CultureInfo.InvariantCulture, $"{notes.Count} notes · {channelText}");
    }
}
