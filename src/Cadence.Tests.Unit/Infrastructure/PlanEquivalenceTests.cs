using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Infrastructure.Projects;
using Cadence.Playback;
using static Cadence.Tests.Unit.Playback.PlanDump;

namespace Cadence.Tests.Unit.Infrastructure;

/// <summary>
/// Guards that projects saved in older formats still send and export the same MIDI 1.0 bytes once read
/// by the current version, however the model behind them changes. The fixtures were written by the
/// format 2 serializer and must never be regenerated; a failure here means old projects play differently.
/// </summary>
public sealed class PlanEquivalenceTests
{
    // The canon fixture is canon-gm16.mid imported and saved without routes, so its hashes equal the
    // sample's hashes in MidiOneOutputGoldenTests.
    public static TheoryData<string, string, string> Fixtures() => new()
    {
        { "format2-full.cadence", "16195787EB3D796E", "A959AF30B339DF00" },
        { "format2-canon-gm16.cadence", "BE918A5129078D96", "6F9AA00B419CABFC" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void OlderFormat_SendsAndExportsTheSameMidiOneBytes(string fixture, string playback, string exported)
    {
        var project = ProjectSerializer.Default.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", fixture))).Project;

        Assert.Equal(playback, Hash(PlaybackStream(project.Sequence, RoutedBindings(project))));
        Assert.Equal(exported, Hash(ExportStream(project.Sequence)));
    }

    // Every track on its own slot, with the channel and transpose of its route.
    private static Dictionary<TrackId, PlanTrackBinding> RoutedBindings(Project project) =>
        project.Sequence.Tracks.Select((t, i) => (t.Id, Slot: i)).ToDictionary(
            x => x.Id,
            x => project.Routing.Routes.TryGetValue(x.Id, out var route)
                ? new PlanTrackBinding(x.Slot, route.Channel, route.Transpose)
                : new PlanTrackBinding(x.Slot));
}
