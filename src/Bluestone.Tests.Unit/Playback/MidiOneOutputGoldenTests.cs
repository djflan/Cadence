using Cadence.Midi.Files;
using static Cadence.Tests.Unit.Playback.PlanDump;

namespace Cadence.Tests.Unit.Playback;

/// <summary>
/// Guards what a MIDI 1.0 device receives from the shipped samples, for playback and for SMF export.
/// See <see cref="PlanDump"/> for how outputs are listed.
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
}
