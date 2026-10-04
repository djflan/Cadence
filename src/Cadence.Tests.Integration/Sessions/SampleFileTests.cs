using Cadence.Application.Sessions;
using Cadence.Domain.Sequencing;

namespace Cadence.Tests.Integration.Sessions;

/// <summary>The shipped demo must import cleanly; it is also a redistributable golden fixture.</summary>
public sealed class SampleFileTests
{
    [Fact]
    public async Task DemoSong_ImportsWithoutDiagnostics()
    {
        var session = new ProjectSession();

        var report = await session.ImportMidiAsync(Path.Combine(RepositoryPaths.Root, "samples", "cadence-demo.mid"), TestContext.Current.CancellationToken);

        Assert.Empty(report.ImportDiagnostics);
        Assert.Equal("Cadence Demo", session.Project.Name);
        Assert.Equal(["Drums", "Bass", "Keys", "Lead"], session.Project.Sequence.Tracks.Select(t => t.Name));
        Assert.Equal(266, session.Project.Sequence.Tracks.Sum(t => t.Events.OfType<NoteEvent>().Count()));
        Assert.Equal("Verse", Assert.Single(session.Project.Sequence.Markers).Name);
    }
}
