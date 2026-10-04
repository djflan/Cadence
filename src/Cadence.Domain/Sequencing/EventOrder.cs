using Cadence.Domain.Midi;

namespace Cadence.Domain.Sequencing;

/// <summary>Canonical ordering rules for simultaneous events.</summary>
public static class EventOrder
{
    public static EventPhase PhaseOf(ChannelMessage message) => message.Kind switch
    {
        _ when message.IsNoteOff => EventPhase.NoteOff,
        ChannelMessageKind.NoteOn => EventPhase.NoteOn,
        ChannelMessageKind.ProgramChange => EventPhase.ProgramChange,
        ChannelMessageKind.ControlChange when message.Controller.IsBankSelect => EventPhase.BankSelect,
        _ => EventPhase.Control,
    };

    /// <summary>
    /// Compares by position, then phase. This is not a total order: ties must be broken by the
    /// caller's stable sequence (track position, then track index).
    /// </summary>
    public static int Compare(TrackEvent left, TrackEvent right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var byPosition = left.Position.CompareTo(right.Position);
        return byPosition != 0 ? byPosition : left.Phase.CompareTo(right.Phase);
    }
}
