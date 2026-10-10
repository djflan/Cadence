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
/// <remarks>
/// <list type="bullet">
/// <item>format2-full: <see cref="ProjectSamples.Full"/>, every event type and routing option. Its mute and
/// solo flags were cleared by hand after writing, so that it plays at all.</item>
/// <item>format2-edges: a note outlasting the last event, events on bar lines, an orphan release and a
/// meta at the last tick, a track starting after a mid-bar meter change, a muted track, an empty track.</item>
/// <item>format2-canon-gm16: canon-gm16.mid imported and saved without routes, so its per-track hashes
/// equal the sample's hashes in MidiOneOutputGoldenTests.</item>
/// </list>
/// Plans are compiled with each route's channel and transpose, once with a slot per track and once with
/// every track on one slot, which pins the order between tracks that share an output. Voice selections
/// depend on resolved profiles, not on the track model, so they are left out.
/// </remarks>
public sealed class PlanEquivalenceTests
{
    public static TheoryData<string, string, string, string> Fixtures() => new()
    {
        { "format2-full.cadence", "16195787EB3D796E", "16195787EB3D796E", "A959AF30B339DF00" },
        { "format2-edges.cadence", "B3A0AB00DA40E3F0", "EC1E2E5597714704", "A9D37B3B99CF7FF3" },
        { "format2-canon-gm16.cadence", "BE918A5129078D96", "C1BD096BB50430FF", "6F9AA00B419CABFC" },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void OlderFormat_SendsAndExportsTheSameMidiOneBytes(string fixture, string slotPerTrack, string sharedSlot, string exported)
    {
        var project = ProjectSerializer.Default.Deserialize(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Fixtures", fixture))).Project;

        Assert.Equal(slotPerTrack, Hash(PlaybackStream(project.Sequence, RoutedBindings(project, shared: false))));
        Assert.Equal(sharedSlot, Hash(PlaybackStream(project.Sequence, RoutedBindings(project, shared: true))));
        Assert.Equal(exported, Hash(ExportStream(project.Sequence)));
    }

    private static Dictionary<TrackId, PlanTrackBinding> RoutedBindings(Project project, bool shared) =>
        project.Sequence.Tracks.Select((t, i) => (t.Id, Slot: shared ? 0 : i)).ToDictionary(
            x => x.Id,
            x => project.Routing.Routes.TryGetValue(x.Id, out var route)
                ? new PlanTrackBinding(x.Slot, route.Channel, route.Transpose)
                : new PlanTrackBinding(x.Slot));
}
