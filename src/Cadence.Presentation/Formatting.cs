using System.Globalization;
using Cadence.Domain.Midi;
using Cadence.Domain.Time;

namespace Cadence.Presentation;

/// <summary>Consistent, culture-invariant display text for musical values.</summary>
public static class Formatting
{
    private static readonly string[] PitchClasses = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    /// <summary>Scientific pitch notation with middle C (60) as C4.</summary>
    public static string NoteName(NoteNumber note) =>
        string.Create(CultureInfo.InvariantCulture, $"{PitchClasses[note.Value % 12]}{(note.Value / 12) - 1}");

    public static string Position(BarBeatTick position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.Bar}.{position.Beat}.{position.Tick:000}");

    public static string Time(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}");

    public static string Tempo(Tempo tempo) =>
        string.Create(CultureInfo.InvariantCulture, $"{tempo.BeatsPerMinute:0.0}");

    public static string Milliseconds(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{time.TotalMilliseconds:0.0} ms");

    /// <summary>Describes a MIDI message for the monitor. SysEx payloads are never shown, only their size and manufacturer.</summary>
    public static string Message(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return "(empty)";
        }

        if (bytes[0] == SysExMessage.Start)
        {
            var manufacturer = bytes.Length > 1 ? bytes[1].ToString("X2", CultureInfo.InvariantCulture) : "?";
            return string.Create(CultureInfo.InvariantCulture, $"SysEx · {bytes.Length} bytes · manufacturer {manufacturer}");
        }

        if (bytes.Length >= 2 && ChannelMessage.TryCreate(bytes[0], bytes[1], bytes.Length > 2 ? bytes[2] : (byte)0, out var m))
        {
            var ch = string.Create(CultureInfo.InvariantCulture, $"Ch {m.Channel.Number}");
            return m.Kind switch
            {
                _ when m.IsNoteOn => string.Create(CultureInfo.InvariantCulture, $"Note On · {ch} · {NoteName(m.Note)} · vel {m.Data2}"),
                _ when m.IsNoteOff => string.Create(CultureInfo.InvariantCulture, $"Note Off · {ch} · {NoteName(m.Note)}"),
                ChannelMessageKind.ControlChange => string.Create(CultureInfo.InvariantCulture, $"CC {m.Data1} · {ch} · {m.Data2}"),
                ChannelMessageKind.ProgramChange => string.Create(CultureInfo.InvariantCulture, $"Program {m.Program.Number} · {ch}"),
                ChannelMessageKind.PitchBend => string.Create(CultureInfo.InvariantCulture, $"Pitch Bend · {ch} · {m.PitchBendValue.OffsetFromCenter:+0;-0;0}"),
                ChannelMessageKind.ChannelPressure => string.Create(CultureInfo.InvariantCulture, $"Pressure · {ch} · {m.Data1}"),
                ChannelMessageKind.PolyPressure => string.Create(CultureInfo.InvariantCulture, $"Poly Pressure · {ch} · {NoteName(m.Note)} · {m.Data2}"),
                _ => Convert.ToHexString(bytes),
            };
        }

        return string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
