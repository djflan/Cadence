using System.Security.Cryptography;
using System.Text;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Files;
using Cadence.Midi.Wire;
using Cadence.Playback;

namespace Cadence.Tests.Unit.Playback;

/// <summary>
/// Guards what a MIDI 1.0 device receives from the shipped samples, for playback and for SMF export.
/// Each output is listed per slot (or file track) and channel, in dispatch order with ticks, because
/// order between different channels at the same tick does not reach any one instrument.
/// </summary>
/// <remarks>
/// If a change is meant to alter what instruments receive, regenerate the hashes and say why in the
/// commit. Otherwise a failure here is a regression.
/// </remarks>
public sealed class MidiOneOutputGoldenTests
{
    public static TheoryData<string, string, string> Samples() => new()
    {
        { "cadence-demo.mid", "52C43EEE4CD69D64", "042D601A18B616FE" },
        { "canon-gm16.mid", "BE918A5129078D96", "6F9AA00B419CABFC" },
        { "canon-gm16-format0.mid", "83140E7057B59333", "D2D4EDA101A8027E" },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Sample_SendsAndExportsTheSameMidiOneBytes(string sample, string playback, string exported)
    {
        var file = SmfReader.Read(File.ReadAllBytes(Path.Combine(SamplesDirectory, sample))).File;
        var sequence = SmfImporter.Import(file).Sequence;

        Assert.Equal(playback, Hash(PlaybackStream(sequence)));
        Assert.Equal(exported, Hash(ExportStream(sequence)));
    }

    private static string SamplesDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root."), "samples");
        }
    }

    // Every track on its own slot; note releases are sent by the engine at the end of each note.
    private static IEnumerable<string> PlaybackStream(Sequence sequence)
    {
        var bindings = sequence.Tracks.Select((t, i) => (t.Id, Slot: i)).ToDictionary(x => x.Id, x => new PlanTrackBinding(x.Slot));
        var plan = PlaybackPlanCompiler.Compile(sequence, bindings);
        var sent = new List<(int Slot, string Channel, long Tick, int Order, string Bytes)>();
        for (var i = 0; i < plan.Events.Length; i++)
        {
            var e = plan.Events[i];
            if (e.PayloadIndex >= 0)
            {
                sent.Add((e.Slot, "sys", e.Tick, i, Convert.ToHexString(plan.Payloads[e.PayloadIndex].Span)));
                continue;
            }

            sent.Add((e.Slot, e.Message.Channel.Number.ToString("00", System.Globalization.CultureInfo.InvariantCulture), e.Tick, i, Hex(e.Message)));
            if (e.IsNote)
            {
                // The engine sends releases before anything else due at the same time.
                sent.Add((e.Slot, e.Message.Channel.Number.ToString("00", System.Globalization.CultureInfo.InvariantCulture), e.Tick + e.DurationTicks, -1, Hex(ChannelMessage.NoteOff(e.Message.Channel, e.Message.Note, e.ReleaseVelocity))));
            }
        }

        return sent.OrderBy(s => s.Slot).ThenBy(s => s.Channel, StringComparer.Ordinal).ThenBy(s => s.Tick).ThenBy(s => s.Order)
            .Select(s => $"{s.Slot} {s.Channel} {s.Tick} {s.Bytes}");
    }

    private static IEnumerable<string> ExportStream(Sequence sequence)
    {
        var exported = SmfReader.Read(SmfWriter.Write(SmfExporter.Export(sequence).File)).File;
        return exported.Tracks.SelectMany((track, t) => track.Events.Select((e, i) => (t, e, i)))
            .Select(x => x.e switch
            {
                SmfChannelEvent c => (x.t, Channel: c.Message.Channel.Number.ToString("00", System.Globalization.CultureInfo.InvariantCulture), x.e.Tick, x.i, Hex(c.Message)),
                SmfSysExEvent s => (x.t, Channel: "sys", x.e.Tick, x.i, "F0" + Convert.ToHexString(s.Data.Span)),
                SmfEscapeEvent s => (x.t, Channel: "sys", x.e.Tick, x.i, "F7" + Convert.ToHexString(s.Data.Span)),
                SmfMetaEvent m => (x.t, Channel: "meta", x.e.Tick, x.i, $"FF{m.Type:X2}" + Convert.ToHexString(m.Data.Span)),
                _ => (x.t, Channel: "?", x.e.Tick, x.i, string.Empty),
            })
            .OrderBy(s => s.t).ThenBy(s => s.Channel, StringComparer.Ordinal).ThenBy(s => s.Tick).ThenBy(s => s.i)
            .Select(s => $"{s.t} {s.Channel} {s.Tick} {s.Item5}");
    }

    private static string Hex(ChannelMessage message)
    {
        Span<byte> bytes = stackalloc byte[3];
        return Convert.ToHexString(bytes[..message.CopyTo(bytes)]);
    }

    private static string Hash(IEnumerable<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..16];
}
