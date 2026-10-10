using System.Collections.ObjectModel;
using System.Globalization;
using Cadence.Application.Editing;
using Cadence.Domain.Devices;
using Cadence.Domain.Mixing;
using Cadence.Domain.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadence.Presentation;

/// <summary>
/// The project's racks: free-standing device chains that several tracks route into, such as one shared XG
/// instrument (ADR 0022). A rack is edited in the device strip and connected in the routing inspector.
/// </summary>
public sealed partial class RacksViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    internal RacksViewModel(MainViewModel owner) => _owner = owner;

    public ObservableCollection<RackRow> Racks { get; } = [];

    public bool IsEmpty => Racks.Count == 0;

    internal void Sync(Project project, DeviceChainId? editing)
    {
        var racks = project.Chains.Where(c => c.Owner.Kind == ChainOwnerKind.Rack).ToList();
        while (Racks.Count > racks.Count)
        {
            Racks.RemoveAt(Racks.Count - 1);
        }

        for (var i = 0; i < racks.Count; i++)
        {
            if (i == Racks.Count)
            {
                Racks.Add(new RackRow(this));
            }

            var sources = project.ConnectionsTo(Domain.Routing.SignalNode.Rack(racks[i].Id)).Count();
            Racks[i].Sync(racks[i], sources, racks[i].Id == editing);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Adds an empty rack and opens it in the device strip.</summary>
    [RelayCommand]
    private void NewRack()
    {
        var rack = DeviceChain.Create(ChainOwner.Rack, string.Create(CultureInfo.InvariantCulture, $"Rack {Racks.Count + 1}"));
        if (_owner.Execute(DeviceCommands.AddRack(rack)))
        {
            _owner.DeviceStrip.EditRack(rack.Id);
        }
    }

    internal void Edit(RackRow rack) => _owner.DeviceStrip.EditRack(rack.Id);

    internal void Remove(RackRow rack) => _owner.Execute(DeviceCommands.RemoveRack(rack.Id));

    internal void Rename(RackRow rack, string name) => _owner.Execute(DeviceCommands.RenameChain(rack.Id, name));
}

/// <summary>One rack in the list.</summary>
public sealed partial class RackRow : ObservableObject
{
    private readonly RacksViewModel _owner;
    private bool _syncing;

    internal RackRow(RacksViewModel owner) => _owner = owner;

    public DeviceChainId Id { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>"2 devices · 4 tracks in".</summary>
    [ObservableProperty]
    public partial string Detail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    internal void Sync(DeviceChain rack, int sources, bool editing)
    {
        _syncing = true;
        try
        {
            Id = rack.Id;
            Name = rack.Name;
            IsEditing = editing;
            Detail = string.Create(CultureInfo.InvariantCulture, $"{rack.Devices.Length} device{(rack.Devices.Length == 1 ? string.Empty : "s")} · {sources} input{(sources == 1 ? string.Empty : "s")}");
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_syncing && value.Length is > 0 and <= DeviceChain.MaxNameLength)
        {
            _owner.Rename(this, value);
        }
    }

    [RelayCommand]
    private void Edit() => _owner.Edit(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}

/// <summary>Where a mixer channel sends its audio: the master, or another channel (a bus).</summary>
public sealed record MixerOutputOption(MixerChannelId? Channel, string Name)
{
    public static readonly MixerOutputOption Master = new(null, "Master");

    public override string ToString() => Name;
}

/// <summary>
/// The mixer: channels that are audio paths, independent of tracks (ADR 0023). Nothing sounds yet (there is no
/// audio engine); the settings are kept, saved, and validated, so they are ready when it exists.
/// </summary>
public sealed partial class MixerViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _syncing;

    internal MixerViewModel(MainViewModel owner) => _owner = owner;

    public ObservableCollection<MixerChannelRow> Channels { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGainText))]
    public partial double MasterGain { get; set; }

    public string MasterGainText => MixerChannelRow.FormatGain(MasterGain);

    public bool IsEmpty => Channels.Count == 0;

    internal DeviceDefinitionLookup Definitions => _owner.Devices.Lookup;

    internal void Sync(Mixer mixer)
    {
        _syncing = true;
        try
        {
            MasterGain = mixer.MasterGainDecibels;
            while (Channels.Count > mixer.Channels.Length)
            {
                Channels.RemoveAt(Channels.Count - 1);
            }

            for (var i = 0; i < mixer.Channels.Length; i++)
            {
                if (i == Channels.Count)
                {
                    Channels.Add(new MixerChannelRow(this));
                }

                Channels[i].Sync(mixer, mixer.Channels[i]);
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnMasterGainChanged(double value)
    {
        if (!_syncing)
        {
            _owner.Execute(RoutingCommands.SetMasterGain(Math.Clamp(value, MixerChannel.MinGainDecibels, MixerChannel.MaxGainDecibels)));
        }
    }

    [RelayCommand]
    private void NewChannel() =>
        _owner.Execute(RoutingCommands.AddMixerChannel(MixerChannel.Create(string.Create(CultureInfo.InvariantCulture, $"Channel {Channels.Count + 1}"))));

    internal void Update(MixerChannel channel, string? mergeKey = null) => _owner.Execute(RoutingCommands.UpdateMixerChannel(channel, Definitions, mergeKey));

    internal void Remove(MixerChannelId channel) => _owner.Execute(RoutingCommands.RemoveMixerChannel(channel));
}

/// <summary>One mixer channel, edited in place.</summary>
public sealed partial class MixerChannelRow : ObservableObject
{
    private readonly MixerViewModel _mixer;
    private MixerChannel _channel = MixerChannel.Create("-");
    private bool _syncing;

    internal MixerChannelRow(MixerViewModel mixer) => _mixer = mixer;

    public MixerChannelId Id => _channel.Id;

    public double MinimumGain { get; } = MixerChannel.MinGainDecibels;

    public double MaximumGain { get; } = MixerChannel.MaxGainDecibels;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double Gain { get; set; }

    [ObservableProperty]
    public partial double Pan { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSoloed { get; set; }

    public ObservableCollection<MixerOutputOption> Outputs { get; } = [];

    [ObservableProperty]
    public partial MixerOutputOption? Output { get; set; }

    public string GainText => FormatGain(Gain);

    internal static string FormatGain(double decibels) => string.Create(CultureInfo.InvariantCulture, $"{decibels:+0.0;-0.0;0.0} dB");

    internal void Sync(Mixer mixer, MixerChannel channel)
    {
        _syncing = true;
        try
        {
            _channel = channel;
            Name = channel.Name;
            Gain = channel.GainDecibels;
            Pan = channel.Pan;
            IsMuted = channel.IsMuted;
            IsSoloed = channel.IsSoloed;
            var options = new List<MixerOutputOption> { MixerOutputOption.Master };
            options.AddRange(mixer.Channels.Where(c => c.Id != channel.Id).Select(c => new MixerOutputOption(c.Id, c.Name)));
            if (!Outputs.SequenceEqual(options))
            {
                Outputs.Clear();
                foreach (var option in options)
                {
                    Outputs.Add(option);
                }
            }

            Output = Outputs.FirstOrDefault(o => o.Channel == channel.Output) ?? MixerOutputOption.Master;
            OnPropertyChanged(nameof(GainText));
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnNameChanged(string value)
    {
        if (!_syncing && value.Length is > 0 and <= MixerChannel.MaxNameLength)
        {
            _mixer.Update(_channel with { Name = value });
        }
    }

    partial void OnGainChanged(double value)
    {
        OnPropertyChanged(nameof(GainText));
        if (!_syncing)
        {
            _mixer.Update(_channel with { GainDecibels = Math.Clamp(value, MinimumGain, MaximumGain) }, $"gain:{Id}");
        }
    }

    partial void OnPanChanged(double value)
    {
        if (!_syncing)
        {
            _mixer.Update(_channel with { Pan = Math.Clamp(value, -1, 1) }, $"pan:{Id}");
        }
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_syncing)
        {
            _mixer.Update(_channel with { IsMuted = value });
        }
    }

    partial void OnIsSoloedChanged(bool value)
    {
        if (!_syncing)
        {
            _mixer.Update(_channel with { IsSoloed = value });
        }
    }

    partial void OnOutputChanged(MixerOutputOption? value)
    {
        if (!_syncing && value is not null)
        {
            _mixer.Update(_channel with { Output = value.Channel });
        }
    }

    [RelayCommand]
    private void Remove() => _mixer.Remove(Id);
}
