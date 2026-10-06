using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Wire;

namespace Cadence.Tests.Unit.Midi.Wire;

public sealed class Midi1EncoderTests
{
    private static readonly MidiChannel Ten = MidiChannel.FromNumber(10);

    private static string Bytes(ChannelMessage m)
    {
        var buffer = new byte[3];
        return Convert.ToHexString(buffer, 0, m.CopyTo(buffer));
    }

    [Fact]
    public void NoteOnAndOff_EncodeAttackAndReleaseVelocity()
    {
        var note = new NoteEvent(EventId.New(), Tick.Zero, new TickSpan(10), Ten, NoteNumber.MiddleC, new Velocity(100), new Velocity(30));

        Assert.Equal("993C64", Bytes(Midi1Encoder.NoteOn(note)));
        Assert.Equal("893C1E", Bytes(Midi1Encoder.NoteOff(note)));
    }

    [Fact]
    public void ProgramSelection_SendsBankMsbThenLsbThenProgram()
    {
        var selection = new ProgramSelection(new ProgramNumber(17), new SevenBitValue(0), new SevenBitValue(64));

        Assert.Equal(["B90000", "B92040", "C911"], Midi1Encoder.ProgramSelection(selection, Ten).Select(Bytes));
    }

    [Fact]
    public void ProgramSelection_SendsOnlyTheBankPartsGiven()
    {
        Assert.Equal(["C900"], Midi1Encoder.ProgramSelection(new ProgramSelection(new ProgramNumber(0)), Ten).Select(Bytes));
        Assert.Equal(["B9007F", "C900"], Midi1Encoder.ProgramSelection(new ProgramSelection(new ProgramNumber(0), BankMsb: SevenBitValue.Max), Ten).Select(Bytes));
    }
}
