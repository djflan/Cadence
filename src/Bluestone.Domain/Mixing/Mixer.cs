using System.Collections.Immutable;

namespace Cadence.Domain.Mixing;

/// <summary>Stable identity of a mixer channel. It is unrelated to any track's identity.</summary>
public readonly record struct MixerChannelId(Guid Value)
{
    public static MixerChannelId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>
/// One audio path in the mixer: where audio from one or more sources is summed, leveled, and sent on.
/// A mixer channel is not a track. Twelve MIDI tracks playing one stereo instrument need one channel,
/// and a hardware-only MIDI track needs none (ADR 0023).
/// </summary>
public sealed record MixerChannel
{
    public const int MaxNameLength = 256;
    public const double MinGainDecibels = -120;
    public const double MaxGainDecibels = 24;

    private readonly string _name = string.Empty;
    private readonly double _gain;
    private readonly double _pan;

    public required MixerChannelId Id { get; init; }

    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Channel names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    public double GainDecibels
    {
        get => _gain;
        init => _gain = value is >= MinGainDecibels and <= MaxGainDecibels ? value : throw new ArgumentOutOfRangeException(nameof(GainDecibels), value, "Gain must be between -120 and 24 dB.");
    }

    /// <summary>-1 is hard left, 0 is center, 1 is hard right.</summary>
    public double Pan
    {
        get => _pan;
        init => _pan = value is >= -1 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(Pan), value, "Pan must be between -1 and 1.");
    }

    public bool IsMuted { get; init; }

    public bool IsSoloed { get; init; }

    /// <summary>The channel this one feeds (a bus), or null for the master.</summary>
    public MixerChannelId? Output { get; init; }

    public static MixerChannel Create(string name) => new() { Id = MixerChannelId.New(), Name = name };
}

/// <summary>The project's mixer: its channels, and the master bus they end in. Immutable.</summary>
public sealed record Mixer
{
    public const int MaxChannels = 1024;

    private readonly ImmutableArray<MixerChannel> _channels = [];
    private readonly double _master;

    public static Mixer Empty { get; } = new();

    public ImmutableArray<MixerChannel> Channels
    {
        get => _channels;
        init
        {
            var channels = value.IsDefault ? [] : value;
            if (channels.Length > MaxChannels)
            {
                throw new ArgumentException($"A mixer holds at most {MaxChannels} channels.", nameof(Channels));
            }

            var ids = new HashSet<MixerChannelId>();
            foreach (var channel in channels)
            {
                ArgumentNullException.ThrowIfNull(channel, nameof(Channels));
                if (!ids.Add(channel.Id))
                {
                    throw new ArgumentException($"Mixer channel {channel.Id} appears more than once.", nameof(Channels));
                }
            }

            _channels = channels;
        }
    }

    public double MasterGainDecibels
    {
        get => _master;
        init => _master = value is >= MixerChannel.MinGainDecibels and <= MixerChannel.MaxGainDecibels
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MasterGainDecibels), value, "Gain must be between -120 and 24 dB.");
    }

    public MixerChannel? Find(MixerChannelId id)
    {
        foreach (var channel in _channels)
        {
            if (channel.Id == id)
            {
                return channel;
            }
        }

        return null;
    }

    /// <summary>Replaces the channel with the same ID, or adds it.</summary>
    public Mixer With(MixerChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var index = -1;
        for (var i = 0; i < _channels.Length; i++)
        {
            if (_channels[i].Id == channel.Id)
            {
                index = i;
                break;
            }
        }

        return this with { Channels = index < 0 ? _channels.Add(channel) : _channels.SetItem(index, channel) };
    }

    /// <summary>Removes a channel, and clears the output of any channel that fed it (those fall back to the master).</summary>
    public Mixer Without(MixerChannelId id) =>
        this with { Channels = [.. _channels.Where(c => c.Id != id).Select(c => c.Output == id ? c with { Output = null } : c)] };
}
