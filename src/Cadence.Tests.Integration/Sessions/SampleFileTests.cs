using Cadence.Application.Sessions;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;

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

    [Fact]
    public async Task SixteenChannelCanon_ImportsEveryChannelWithoutDiagnostics()
    {
        var session = new ProjectSession();

        var report = await session.ImportMidiAsync(Path.Combine(RepositoryPaths.Root, "samples", "canon-gm16.mid"), TestContext.Current.CancellationToken);

        Assert.Empty(report.ImportDiagnostics);
        Assert.Equal("Canon in D (after Pachelbel)", session.Project.Name);
        var sequence = session.Project.Sequence;
        Assert.Equal(480, sequence.Ppqn.TicksPerQuarterNote);
        Assert.Equal(
            ["Violin I", "Violin II", "Viola", "Cello", "Contrabass", "Harpsichord", "Piano", "Strings",
             "Flute", "Drums", "Oboe", "French Horn", "Harp", "Choir", "Timpani", "Glockenspiel"],
            sequence.Tracks.Select(t => t.Name));

        // Expected values come from samples/canon-gm16.py, not from Cadence.
        int[] notes = [97, 89, 81, 65, 29, 195, 148, 100, 33, 88, 11, 21, 166, 23, 24, 9];
        int[] programs = [40, 40, 41, 42, 43, 6, 0, 48, 73, 0, 68, 60, 46, 52, 47, 9];
        for (var i = 0; i < 16; i++)
        {
            var events = sequence.Tracks[i].Events;
            var channelMessages = events.OfType<ChannelEvent>().Select(e => e.Message).ToList();
            Assert.Equal(notes[i], events.OfType<NoteEvent>().Count());
            Assert.All(events.OfType<NoteEvent>(), n => Assert.Equal(i, n.Channel.Index));
            Assert.All(channelMessages, m => Assert.Equal(i, m.Channel.Index));
            Assert.Equal(programs[i], Assert.Single(channelMessages, m => m.Kind == ChannelMessageKind.ProgramChange).Program.Value);
        }

        Assert.Equal(1179, sequence.Tracks.Sum(t => t.Events.OfType<NoteEvent>().Count()));
        Assert.Equal(32160, sequence.EndPosition.Value);
        Assert.Equal([(0L, 750_000), (26880L, 800_000), (30720L, 1_000_000)], sequence.TempoMap.Changes.Select(c => (c.Position.Value, c.Tempo.MicrosecondsPerQuarterNote)));
        Assert.Equal([(0L, new TimeSignature(4, 4)), (30720L, new TimeSignature(3, 4))], sequence.MeterMap.Changes.Select(c => (c.Position.Value, c.Signature)));
        Assert.Equal(["Ground", "Canon", "Tutti", "Coda"], sequence.Markers.Select(m => m.Name));

        var violin = sequence.Tracks[0].Events;
        Assert.Equal<byte>([0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7], Assert.Single(violin.OfType<SysExEvent>()).Message.Bytes.ToArray());
        Assert.Equal<byte>([2, 0], Assert.Single(violin.OfType<MetaEvent>(), m => m.Type == 0x59).Data.ToArray());
        Assert.Equal(9, sequence.Tracks[8].Events.OfType<ChannelEvent>().Count(e => e.Message.Kind == ChannelMessageKind.PitchBend));
        Assert.Equal(21, sequence.Tracks[10].Events.OfType<ChannelEvent>().Count(e => e.Message.Kind == ChannelMessageKind.ChannelPressure));
        Assert.Equal(11, sequence.Tracks[13].Events.OfType<MetaEvent>().Count(m => m.Type == 0x05));
    }
}
