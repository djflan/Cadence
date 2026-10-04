using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

namespace Cadence.Playback;

/// <summary>Where a track's events go in a plan: an output slot and an optional channel override.</summary>
/// <param name="OutputSlot">Index into the outputs given to <see cref="PlaybackEngine.SetOutputs"/>.</param>
/// <param name="Channel">When set, every channel message on the track is re-addressed to this channel.</param>
public sealed record PlanTrackBinding(int OutputSlot, MidiChannel? Channel = null);

/// <summary>Something the compiler left out of a plan, and why.</summary>
public sealed record PlanDiagnostic(TrackId Track, string Message);

/// <summary>One dispatchable message in a plan.</summary>
internal readonly struct PlanEvent(long tick, int slot, ChannelMessage message, int payloadIndex, long durationTicks, Velocity releaseVelocity = default)
{
    public readonly long Tick = tick;
    public readonly int Slot = slot;

    /// <summary>The channel message, when <see cref="PayloadIndex"/> is negative.</summary>
    public readonly ChannelMessage Message = message;

    /// <summary>Index into <see cref="PlaybackPlan.Payloads"/> for SysEx and other byte messages, or -1.</summary>
    public readonly int PayloadIndex = payloadIndex;

    /// <summary>For paired notes, the duration after which the engine releases the note; otherwise 0.</summary>
    public readonly long DurationTicks = durationTicks;

    /// <summary>For paired notes, the velocity of the release the engine sends.</summary>
    public readonly Velocity ReleaseVelocity = releaseVelocity;

    public bool IsNote => DurationTicks > 0;
}

/// <summary>
/// An immutable, prepared snapshot of everything playback needs: messages in canonical dispatch order
/// (ADR 0003) with their output slots, plus the tempo map. Edits produce a new plan; the engine swaps
/// plans atomically, so the UI can never race the scheduler.
/// </summary>
public sealed class PlaybackPlan
{
    internal PlaybackPlan(TempoMap tempoMap, PlanEvent[] events, ByteBlock[] payloads, IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        TempoMap = tempoMap;
        Events = events;
        Payloads = payloads;
        Diagnostics = diagnostics;
    }

    public static PlaybackPlan Empty(TempoMap tempoMap) => new(tempoMap, [], [], []);

    public TempoMap TempoMap { get; }

    public int EventCount => Events.Length;

    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    internal PlanEvent[] Events { get; }

    internal ByteBlock[] Payloads { get; }

    /// <summary>Index of the first event at or after <paramref name="tick"/>.</summary>
    internal int FirstIndexAtOrAfter(long tick)
    {
        int low = 0, high = Events.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Events[mid].Tick < tick)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
