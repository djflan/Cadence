using Cadence.Domain.Midi;
using Cadence.Midi.Endpoints;

namespace Cadence.Presentation;

/// <summary>An output choice. A remembered but disconnected endpoint is listed so the route stays visible.</summary>
public sealed record OutputOption(EndpointId? Id, string Name, string Detail, bool IsAvailable)
{
    public static readonly OutputOption None = new(null, "No output", "The track is silent", true);

    public override string ToString() => Name;
}

/// <summary>A profile choice; <see cref="Id"/> is null for "no profile".</summary>
public sealed record ProfileOption(string? Id, string Name, bool IsInstalled)
{
    public static readonly ProfileOption None = new(null, "No profile", true);

    public override string ToString() => Name;
}

/// <summary>A channel choice; <see cref="Channel"/> is null to keep each event's own channel.</summary>
public sealed record ChannelOption(MidiChannel? Channel)
{
    public static readonly IReadOnlyList<ChannelOption> All =
        [new((MidiChannel?)null), .. MidiChannel.All.Select(c => new ChannelOption(c))];

    public string Name => Channel is { } c ? $"Channel {c.Number}" : "Track channels";

    public override string ToString() => Name;
}

public sealed record BankOption(string? Id, string Name)
{
    public static readonly BankOption None = new(null, "No voice change");

    public override string ToString() => Name;
}

public sealed record ProgramOption(ProgramNumber Program, string Name)
{
    public override string ToString() => Name;
}

public enum RouteHealth
{
    /// <summary>Playing to a connected output.</summary>
    Ready,

    /// <summary>Playable, but something needs attention (name match, missing profile).</summary>
    Attention,

    /// <summary>Not playable: no output, or the output is missing.</summary>
    Offline,
}
