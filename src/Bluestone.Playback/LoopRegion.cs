using Cadence.Domain.Time;

namespace Cadence.Playback;

/// <summary>A half-open loop range: playback wraps from <see cref="End"/> back to <see cref="Start"/>.</summary>
public sealed record LoopRegion
{
    public LoopRegion(Tick start, Tick end)
    {
        if (end <= start)
        {
            throw new ArgumentException("A loop must end after it starts.", nameof(end));
        }

        Start = start;
        End = end;
    }

    public Tick Start { get; }

    public Tick End { get; }
}

public enum TransportState
{
    Stopped,
    Playing,
}
