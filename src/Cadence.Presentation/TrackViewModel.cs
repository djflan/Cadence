using System.Collections.ObjectModel;
using System.Globalization;
using Cadence.Application.Editing;
using Cadence.Application.Routing;
using Cadence.Domain.Midi;
using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Endpoints;
using Cadence.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadence.Presentation;

/// <summary>
/// One track in the track list and inspector. Setting a property issues an undoable project command;
/// <see cref="Sync"/> pushes the project state back without issuing commands.
/// </summary>
public sealed partial class TrackViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _syncing;
    private TrackRoute? _route;
    private DeviceProfile? _profile;

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

    /// <summary>One-based position in the track list.</summary>
    [ObservableProperty]
    public partial int Number { get; private set; }

    /// <summary>Index into the theme's track colour palette.</summary>
    public int ColorIndex => (Number - 1) % 8;

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public ObservableCollection<OutputOption> OutputChoices { get; } = [];

    public ObservableCollection<ProfileOption> ProfileChoices { get; } = [];

#pragma warning disable CA1822 // Bound from XAML, which needs an instance member.
    public IReadOnlyList<ChannelOption> ChannelChoices => ChannelOption.All;
#pragma warning restore CA1822

    public ObservableCollection<BankOption> Banks { get; } = [];

    public ObservableCollection<ProgramOption> Programs { get; } = [];

    [ObservableProperty]
    public partial OutputOption? Output { get; set; }

    [ObservableProperty]
    public partial ProfileOption? Profile { get; set; }

    [ObservableProperty]
    public partial ChannelOption? Channel { get; set; }

    [ObservableProperty]
    public partial int Transpose { get; set; }

    [ObservableProperty]
    public partial BankOption? VoiceBank { get; set; }

    [ObservableProperty]
    public partial ProgramOption? VoiceProgram { get; set; }

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

    /// <summary>"Profile → Output", showing the two independent choices side by side.</summary>
    [ObservableProperty]
    public partial string RouteText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string> ProfileNotices { get; private set; } = [];

    public bool CanInitialize => _profile is { Initialization.Length: > 0 } && Health != RouteHealth.Offline;

    internal void Sync(int number, Track track, ResolvedRoute? resolved, IReadOnlyList<OutputOption> outputs, ProfileCatalog profiles)
    {
        _syncing = true;
        try
        {
            Number = number;
            Name = track.Name;
            IsMuted = track.IsMuted;
            IsSoloed = track.IsSoloed;
            Summary = Summarize(track);
            _route = resolved?.Route;
            _profile = resolved?.Profile.Profile;

            SyncOutputs(outputs, resolved);
            SyncProfiles(profiles, resolved);
            Channel = ChannelOption.All.First(c => c.Channel == _route?.Channel);
            Transpose = _route?.Transpose ?? 0;
            SyncVoice();

            Health = resolved switch
            {
                null or { CanPlay: false } => RouteHealth.Offline,
                { Problems.Length: > 0 } => RouteHealth.Attention,
                _ => RouteHealth.Ready,
            };
            Problems = resolved?.Problems ?? [];
            ProfileNotices = _profile?.Notices ?? [];
            RouteText = $"{Profile?.Name ?? ProfileOption.None.Name} → {Output?.Name ?? OutputOption.None.Name}";
            OnPropertyChanged(nameof(HealthText));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(NeedsAttention));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(ColorIndex));
            OnPropertyChanged(nameof(CanInitialize));
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

    partial void OnOutputChanged(OutputOption? value)
    {
        if (_syncing || value is null || (value.Id is not null && !value.IsAvailable))
        {
            return;
        }

        var endpoint = value.Id is { } id ? _owner.FindEndpoint(id) : null;
        UpdateRoute(r => r with { Endpoint = endpoint is null ? null : RouteResolver.ReferenceTo(endpoint) });
    }

    partial void OnProfileChanged(ProfileOption? value)
    {
        if (_syncing || value is null || !value.IsInstalled)
        {
            return;
        }

        var profile = value.Id is { } id ? _owner.FindProfile(id) : null;
        UpdateRoute(r => r with
        {
            Profile = profile is null ? null : RouteResolver.ReferenceTo(profile),
            Voice = profile?.FindBank(r.Voice?.BankId ?? string.Empty) is null ? null : r.Voice,
        });
    }

    partial void OnChannelChanged(ChannelOption? value)
    {
        if (!_syncing && value is not null)
        {
            UpdateRoute(r => r with { Channel = value.Channel });
        }
    }

    partial void OnTransposeChanged(int value)
    {
        if (!_syncing)
        {
            UpdateRoute(r => r with { Transpose = Math.Clamp(value, -TrackRoute.MaxTranspose, TrackRoute.MaxTranspose) });
        }
    }

    partial void OnVoiceBankChanged(BankOption? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        UpdateRoute(r => r with { Voice = value.Id is { } bank ? new VoiceAssignment(bank, VoiceProgram?.Program ?? new ProgramNumber(0)) : null });
    }

    partial void OnVoiceProgramChanged(ProgramOption? value)
    {
        if (!_syncing && value is not null && VoiceBank?.Id is { } bank)
        {
            UpdateRoute(r => r with { Voice = new VoiceAssignment(bank, value.Program) });
        }
    }

    private void UpdateRoute(Func<TrackRoute, TrackRoute> change) =>
        _owner.Execute(ProjectCommands.SetRoute(change(_route ?? new TrackRoute(Id))));

    private void SyncOutputs(IReadOnlyList<OutputOption> outputs, ResolvedRoute? resolved)
    {
        OutputChoices.Clear();
        OutputChoices.Add(OutputOption.None);
        foreach (var output in outputs)
        {
            OutputChoices.Add(output);
        }

        if (_route?.Endpoint is not { } reference)
        {
            Output = OutputOption.None;
        }
        else if (resolved?.Endpoint.Endpoint is { } bound && OutputChoices.FirstOrDefault(o => o.Id == bound.Id) is { } match)
        {
            Output = match;
        }
        else
        {
            var placeholder = new OutputOption(new EndpointId(reference.ProviderId, reference.EndpointKey), reference.DisplayName ?? reference.EndpointKey, "Disconnected", false);
            OutputChoices.Add(placeholder);
            Output = placeholder;
        }
    }

    private void SyncProfiles(ProfileCatalog catalog, ResolvedRoute? resolved)
    {
        ProfileChoices.Clear();
        ProfileChoices.Add(ProfileOption.None);
        foreach (var profile in catalog.Profiles)
        {
            ProfileChoices.Add(new ProfileOption(profile.Id, profile.Name, true));
        }

        if (_route?.Profile is not { } reference)
        {
            Profile = ProfileOption.None;
        }
        else if (resolved?.Profile.Profile is { } installed)
        {
            Profile = ProfileChoices.First(p => p.Id == installed.Id);
        }
        else
        {
            var placeholder = new ProfileOption(reference.ProfileId, $"{reference.DisplayName ?? reference.ProfileId} (not installed)", false);
            ProfileChoices.Add(placeholder);
            Profile = placeholder;
        }
    }

    private void SyncVoice()
    {
        Banks.Clear();
        Banks.Add(BankOption.None);
        foreach (var bank in _profile?.Banks ?? [])
        {
            Banks.Add(new BankOption(bank.Id, bank.Name));
        }

        VoiceBank = Banks.FirstOrDefault(b => b.Id == _route?.Voice?.BankId) ?? BankOption.None;

        Programs.Clear();
        var selectedBank = _profile?.FindBank(VoiceBank.Id ?? string.Empty);
        if (selectedBank is not null)
        {
            var named = selectedBank.Programs.ToDictionary(p => p.Program);
            for (var value = 0; value < 128; value++)
            {
                var program = new ProgramNumber(value);
                var label = named.TryGetValue(program, out var p)
                    ? string.Create(CultureInfo.InvariantCulture, $"{program.Number} · {p.Name}")
                    : string.Create(CultureInfo.InvariantCulture, $"Program {program.Number}");
                Programs.Add(new ProgramOption(program, label));
            }
        }

        VoiceProgram = _route?.Voice is { } voice ? Programs.FirstOrDefault(p => p.Program == voice.Program) : null;
    }

    private static string Summarize(Track track)
    {
        var notes = track.Events.OfType<NoteEvent>().ToList();
        var channels = notes.Select(n => n.Channel.Number)
            .Concat(track.Events.OfType<ChannelEvent>().Select(c => c.Message.Channel.Number))
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
