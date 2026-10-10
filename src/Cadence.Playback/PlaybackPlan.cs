using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Wire;
using Cadence.Signal;

namespace Cadence.Playback;

/// <summary>
/// One stream of events for one output: what the signal graph delivers to one external instrument part,
/// already processed by its device chains and channel mapping (ADR 0023).
/// </summary>
/// <param name="OutputSlot">Index into the outputs given to <see cref="PlaybackEngine.SetOutputs"/>.</param>
/// <param name="Events">The events in canonical order, keyed by origin track and index (see <see cref="SignalEvent"/>).</param>
public sealed record PlanPart(int OutputSlot, ImmutableArray<SignalEvent> Events)
{
    /// <summary>
    /// Channel events sent at tick 0 (whatever their own position), such as a voice selection. Give them
    /// negative sequence numbers so they precede the part's events in the same phase.
    /// </summary>
    public ImmutableArray<SignalEvent> InitialEvents { get; init; } = [];
}

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
    internal PlaybackPlan(TempoMap tempoMap, MeterMap meterMap, PlanEvent[] events, ByteBlock[] payloads, IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        TempoMap = tempoMap;
        MeterMap = meterMap;
        Events = events;
        Payloads = payloads;
        Diagnostics = diagnostics;
    }

    public static PlaybackPlan Empty(TempoMap tempoMap)
    {
        ArgumentNullException.ThrowIfNull(tempoMap);
        return new(tempoMap, MeterMap.Constant(tempoMap.Ppqn, TimeSignature.CommonTime), [], [], []);
    }

    public TempoMap TempoMap { get; }

    /// <summary>The meter map, used to place metronome clicks on beats.</summary>
    public MeterMap MeterMap { get; }

    public int EventCount => Events.Length;

    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    internal PlanEvent[] Events { get; }

    internal ByteBlock[] Payloads { get; }

    /// <summary>
    /// The first beat at or after <paramref name="tick"/>, and whether it is a bar's downbeat. A meter
    /// change that cuts a bar short starts a new bar, as in <see cref="MeterMap"/>.
    /// </summary>
    internal (long Tick, bool Downbeat) NextBeat(long tick)
    {
        var position = new Tick(Math.Max(0, tick));
        var bar = MeterMap.BarStart(position).Value;
        var beat = MeterMap.SignatureAt(position).TryGetTicksPerBeat(MeterMap.Ppqn, out var span) ? span.Value : MeterMap.Ppqn.TicksPerQuarterNote;
        var candidate = bar + (((position.Value - bar + beat - 1) / beat) * beat);
        var candidateBar = MeterMap.BarStart(new Tick(candidate)).Value;
        if (candidateBar > position.Value && candidateBar < candidate)
        {
            candidate = candidateBar;
        }

        return (candidate, candidate == candidateBar);
    }

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
