using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;

namespace Cadence.Midi.Wire;

/// <summary>
/// The MIDI 1.0 encoding of Cadence's semantic operations. Anything that needs more than one MIDI 1.0
/// message, or a MIDI-1-specific convention, is decided here rather than in the domain or profiles.
/// </summary>
public static class Midi1Encoder
{
    /// <summary>The note-on that starts <paramref name="note"/>.</summary>
    public static ChannelMessage NoteOn(NoteEvent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return ChannelMessage.NoteOn(note.Channel, note.Note, note.Velocity);
    }

    /// <summary>The note-off that releases <paramref name="note"/>, with its release velocity.</summary>
    public static ChannelMessage NoteOff(NoteEvent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return ChannelMessage.NoteOff(note.Channel, note.Note, note.ReleaseVelocity);
    }

    /// <summary>
    /// Bank select MSB (CC 0) and LSB (CC 32) when the selection names them, then the program change,
    /// in that order: a bank takes effect only at the next program change.
    /// </summary>
    public static ImmutableArray<ChannelMessage> ProgramSelection(ProgramSelection selection, MidiChannel channel)
    {
        var messages = ImmutableArray.CreateBuilder<ChannelMessage>(3);
        if (selection.BankMsb is { } msb)
        {
            messages.Add(ChannelMessage.ControlChange(channel, ControllerNumber.BankSelectMsb, msb));
        }

        if (selection.BankLsb is { } lsb)
        {
            messages.Add(ChannelMessage.ControlChange(channel, ControllerNumber.BankSelectLsb, lsb));
        }

        messages.Add(ChannelMessage.ProgramChange(channel, selection.Program));
        return messages.ToImmutable();
    }
}
