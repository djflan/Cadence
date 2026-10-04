using Cadence.Domain.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Domain.Projects;

/// <summary>Stable identity of a project.</summary>
public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>A half-open range of ticks, such as a loop.</summary>
public sealed record TickRange
{
    public TickRange(Tick start, Tick end)
    {
        if (end <= start)
        {
            throw new ArgumentException("A range must end after it starts.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public Tick Start { get; }

    public Tick End { get; }
}

/// <summary>
/// Everything a musician saves: the sequence, how each track is routed, and transport settings.
/// Routes are kept even when their profile or endpoint is unavailable. Immutable.
/// </summary>
public sealed record Project
{
    public const int MaxNameLength = 256;

    private readonly string _name = "Untitled";
    private readonly Sequence _sequence = Sequence.CreateEmpty(Ppqn.Default);

    public required ProjectId Id { get; init; }

    public string Name
    {
        get => _name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _name = value.Length <= MaxNameLength ? value : throw new ArgumentException($"Project names are limited to {MaxNameLength} characters.", nameof(Name));
        }
    }

    public Sequence Sequence
    {
        get => _sequence;
        init => _sequence = value ?? throw new ArgumentNullException(nameof(Sequence));
    }

    public RoutingTable Routing { get; init; } = RoutingTable.Empty;

    public TickRange? Loop { get; init; }

    public static Project CreateNew(string name = "Untitled") => new() { Id = ProjectId.New(), Name = name };
}
