using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Files;
using CsCheck;
using static Cadence.Tests.Unit.Midi.Files.SmfBytes;
using static Cadence.Tests.Unit.Midi.Files.SmfReaderTests;

namespace Cadence.Tests.Unit.Midi.Files;

public sealed class SmfImportExportTests
{
    private static readonly MidiChannel One = MidiChannel.FromIndex(0);

    private static SmfImportResult ImportBytes(byte[] bytes) => SmfImporter.Import(SmfReader.Read(bytes).File);

    [Fact]
    public void Import_PairsOverlappingNotesFirstInFirstOut()
    {
        var result = ImportBytes(File(0, 96, MTrk(
            0x00, 0x90, 0x3C, 0x64,   // on A @0
            0x0A, 0x90, 0x3C, 0x50,   // on B @10
            0x0A, 0x80, 0x3C, 0x20,   // off @20 → A
            0x0A, 0x90, 0x3C, 0x00))); // off (vel 0) @30 → B

        var notes = result.Sequence.Tracks.Single().Events.Cast<NoteEvent>().ToList();
        Assert.Equal([(0L, 20L, 100, 0x20), (10L, 20L, 80, 0)], notes.Select(n => (n.Position.Value, n.Duration.Value, (int)n.Velocity.Value, (int)n.ReleaseVelocity.Value)));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Import_KeepsUnpairedNoteOffsRaw()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0x80, 0x3C, 0x40)));

        var raw = Assert.IsType<ChannelEvent>(result.Sequence.Tracks.Single().Events.Single());
        Assert.True(raw.Message.IsNoteOff);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.UnpairedNoteOff, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Import_EndsUnterminatedNotesAtTrackEnd()
    {
        byte[] body = [0x00, 0x90, 0x3C, 0x64, 0x83, 0x00, 0xFF, 0x2F, 0x00]; // end of track at 384
        var result = ImportBytes(File(0, 96, body));

        var note = Assert.IsType<NoteEvent>(result.Sequence.Tracks.Single().Events.Single());
        Assert.Equal(new TickSpan(384), note.Duration);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.UnterminatedNote);
    }

    [Fact]
    public void Import_LengthensZeroLengthNotes()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0x90, 0x3C, 0x64, 0x00, 0x80, 0x3C, 0x40)));

        Assert.Equal(new TickSpan(1), Assert.IsType<NoteEvent>(result.Sequence.Tracks.Single().Events.Single()).Duration);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.ZeroLengthNote);
    }

    [Fact]
    public void Import_ExtractsConductorDataAndTitle()
    {
        byte[] conductor = MTrk(
            0x00, 0xFF, 0x03, 0x04, (byte)'S', (byte)'o', (byte)'n', (byte)'g',
            0x00, 0xFF, 0x58, 0x04, 0x03, 0x02, 0x18, 0x08,
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20,
            0x60, 0xFF, 0x06, 0x05, (byte)'V', (byte)'e', (byte)'r', (byte)'s', (byte)'e',
            0x00, 0xFF, 0x51, 0x03, 0x0F, 0x42, 0x40);
        byte[] part = MTrk(0x00, 0xFF, 0x03, 0x04, (byte)'B', (byte)'a', (byte)'s', (byte)'s', 0x00, 0x91, 0x28, 0x64, 0x60, 0x81, 0x28, 0x40);

        var result = ImportBytes(File(1, 96, conductor, part));
        var sequence = result.Sequence;

        Assert.Equal("Song", result.Title);
        Assert.Equal("Bass", Assert.Single(sequence.Tracks).Name);
        Assert.Equal([new TempoChange(Tick.Zero, new Tempo(500_000)), new TempoChange(new Tick(96), new Tempo(1_000_000))], sequence.TempoMap.Changes);
        Assert.Equal(new TimeSignature(3, 4), sequence.MeterMap.SignatureAt(Tick.Zero));
        Assert.Equal([new Marker(new Tick(96), "Verse")], sequence.Markers);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Import_ReportsNonStandardMetronomeSettings()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0xFF, 0x58, 0x04, 0x06, 0x03, 0x24, 0x08)));

        Assert.Equal(new TimeSignature(6, 8), result.Sequence.MeterMap.SignatureAt(Tick.Zero));
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.MetronomeSettingsDropped, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Import_KeepsMalformedConductorEventsRaw()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0xFF, 0x51, 0x02, 0x07, 0xA1, 0x00, 0xFF, 0x58, 0x02, 0x04, 0x02)));

        Assert.Equal(2, result.Sequence.Tracks.Single().Events.OfType<MetaEvent>().Count());
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTempo);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTimeSignature);
    }

    [Fact]
    public void Import_KeepsMetersIncompatibleWithResolutionRaw()
    {
        var result = ImportBytes(File(0, 1, MTrk(0x00, 0xFF, 0x58, 0x04, 0x03, 0x03, 0x18, 0x08)));

        Assert.Equal(TimeSignature.CommonTime, result.Sequence.MeterMap.SignatureAt(Tick.Zero));
        Assert.Equal(SmfMetaType.TimeSignature, Assert.IsType<MetaEvent>(result.Sequence.Tracks.Single().Events.Single()).Type);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.InvalidTimeSignature);
    }

    [Theory]
    [InlineData(0xE728, 500, 500_000)]  // 25 fps × 40
    [InlineData(0xE250, 1200, 500_000)] // 30 fps × 80
    [InlineData(0xE350, 1200, 500_500)] // 29.97 drop-frame × 80
    [InlineData(0xE719, 625, 1_000_000)] // 25 fps × 25 → 625 ticks/s, odd
    public void Import_ConvertsSmpteTiming(int division, int expectedPpqn, int expectedTempo)
    {
        var result = ImportBytes(File(0, division, MTrk(0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20)));

        Assert.Equal(expectedPpqn, result.Sequence.Ppqn.TicksPerQuarterNote);
        Assert.Equal(new Tempo(expectedTempo), Assert.Single(result.Sequence.TempoMap.Changes).Tempo);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.SmpteTimingConverted, SmfDiagnosticSeverity.Info);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.TempoIgnoredForSmpte);
    }

    [Fact]
    public void Import_ConvertsCompleteSysExAndKeepsSplitPacketsRaw()
    {
        var result = ImportBytes(File(0, 96, MTrk(
            0x00, 0xF0, 0x08, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7,
            0x00, 0xF0, 0x02, 0x43, 0x10,
            0x05, 0xF7, 0x02, 0x4C, 0xF7)));

        var events = result.Sequence.Tracks.Single().Events;
        Assert.IsType<SysExEvent>(events[0]);
        Assert.Equal([0xF0, 0x43, 0x10], Assert.IsType<RawMidiEvent>(events[1]).Bytes.ToArray());
        Assert.Equal([0x4C, 0xF7], Assert.IsType<RawMidiEvent>(events[2]).Bytes.ToArray());
        Assert.Equal(2, Assert.Single(result.Diagnostics).Count);
    }

    [Fact]
    public void Import_PreservesFileOrderAmongTies()
    {
        var result = ImportBytes(File(0, 96, MTrk(0x00, 0xB0, 0x65, 0x00, 0x00, 0x64, 0x00, 0x00, 0x06, 0x0C)));

        Assert.Equal([101, 100, 6], result.Sequence.Tracks.Single().Events.Cast<ChannelEvent>().Select(e => (int)e.Message.Data1));
    }

    [Fact]
    public void Import_ReportsMultiSequenceAndPortAssignments()
    {
        var result = ImportBytes(File(2, 96, MTrk(0x00, 0xFF, 0x21, 0x01, 0x01), MTrk()));

        Assert.Equal(2, result.Sequence.Tracks.Length);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.MultiSequenceFlattened);
        AssertCode(result.Diagnostics, SmfDiagnosticCodes.PortAssignmentNotApplied, SmfDiagnosticSeverity.Info);
    }

    [Fact]
    public void Import_DecodesLatin1NamesThatAreNotUtf8() =>
        Assert.Equal("Café", ImportBytes(File(0, 96, MTrk(0x00, 0xFF, 0x03, 0x04, (byte)'C', (byte)'a', (byte)'f', 0xE9))).Sequence.Tracks.Single().Name);

    [Fact]
    public void Export_OrdersReleasesBeforeRetriggers()
    {
        var first = new NoteEvent(Tick.Zero, new TickSpan(96), One, NoteNumber.MiddleC, Velocity.Max);
        var second = new NoteEvent(new Tick(96), new TickSpan(96), One, NoteNumber.MiddleC, Velocity.Max);
        var sequence = Sequence.CreateEmpty(new Ppqn(96)).WithTrack(new Track(TrackId.New(), "", [second, first]));

        var track = SmfExporter.Export(sequence).File.Tracks.Skip(1).Single();

        var messages = track.Events.Cast<SmfChannelEvent>().Select(e => (e.Tick, e.Message.IsNoteOn)).ToList();
        Assert.Equal([(0L, true), (96L, false), (96L, true), (192L, false)], messages);
        Assert.Equal(192, track.EndTick);
    }

    [Fact]
    public void Export_WritesConductorTrack()
    {
        var sequence = Sequence.CreateEmpty(new Ppqn(480))
            .WithTempoMap(new TempoMap(new Ppqn(480), [new TempoChange(new Tick(960), new Tempo(400_000))]))
            .WithMarkers([new Marker(new Tick(10), "Intro")]);

        var file = SmfExporter.Export(sequence, "Demo").File;

        Assert.Equal(SmfFormat.MultiTrack, file.Format);
        var conductor = file.Tracks.First().Events.Cast<SmfMetaEvent>().ToList();
        Assert.Equal(
            [(0L, SmfMetaType.TrackName), (0L, SmfMetaType.TimeSignature), (0L, SmfMetaType.Tempo), (10L, SmfMetaType.Marker), (960L, SmfMetaType.Tempo)],
            conductor.Select(e => (e.Tick, e.Type)));
        Assert.Equal([0x04, 0x02, 0x18, 0x08], conductor[1].Data.ToArray());
        Assert.Equal([0x06, 0x1A, 0x80], conductor[4].Data.ToArray());
    }

    [Fact]
    public void Export_ReportsWhatCannotBeStored()
    {
        var sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(Track.Create("Bässe").WithMuted(true));

        var diagnostics = SmfExporter.Export(sequence).Diagnostics;

        AssertCode(diagnostics, SmfDiagnosticCodes.MixerStateNotStored, SmfDiagnosticSeverity.Info);
        AssertCode(diagnostics, SmfDiagnosticCodes.TextEncodedAsUtf8, SmfDiagnosticSeverity.Info);
    }

    private static readonly Gen<TrackEvent> GenTrackEvent =
        Gen.Frequency(
            (5, Gen.Select(Gen.Long[0, 4000], Gen.Long[1, 500], Gen.Int[0, 15], Gen.Int[0, 127], Gen.Int[1, 127], Gen.Int[0, 127])
                .Select(x => (TrackEvent)new NoteEvent(EventId.New(), new Tick(x.Item1), new TickSpan(x.Item2), MidiChannel.FromIndex(x.Item3), new NoteNumber(x.Item4), new Velocity(x.Item5), new Velocity(x.Item6)))),
            (3, Gen.Select(Gen.Long[0, 4000], Gen.Int[0, 15], Gen.Int[0, 127], Gen.Int[0, 127])
                .Select(x => (TrackEvent)new ChannelEvent(new Tick(x.Item1), ChannelMessage.ControlChange(MidiChannel.FromIndex(x.Item2), new ControllerNumber(x.Item3), new SevenBitValue(x.Item4))))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Int[0, 15], Gen.Int[0, 16383])
                .Select(x => (TrackEvent)new ChannelEvent(new Tick(x.Item1), ChannelMessage.PitchBend(MidiChannel.FromIndex(x.Item2), new FourteenBitValue(x.Item3))))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Byte[0, 127].Array[0, 16])
                .Select(x => (TrackEvent)new SysExEvent(new Tick(x.Item1), SysExMessage.Create([0xF0, .. x.Item2, 0xF7])))),
            (1, Gen.Select(Gen.Long[0, 4000], Gen.Byte[0x01, 0x05], Gen.Byte[0x20, 0x7E].Array[0, 12])
                .Where(x => x.Item2 != SmfMetaType.TrackName || x.Item1 != 0)
                .Select(x => (TrackEvent)new MetaEvent(EventId.New(), new Tick(x.Item1), x.Item2, ByteBlock.Copy(x.Item3)))));

    private static readonly Gen<Sequence> GenSequence =
        Gen.Select(
            Gen.Select(Gen.String[Gen.Char['a', 'z'], 0, 12], GenTrackEvent.Array[0, 30]).Array[1, 4],
            Gen.Select(Gen.Long[1, 4000], Gen.Int[200_000, 1_500_000]).Array[0, 4],
            Gen.Select(Gen.Long[1, 4000], Gen.Int[1, 12], Gen.Int[0, 3]).Array[0, 3],
            (tracks, tempos, meters) =>
            {
                var ppqn = new Ppqn(480);
                return new Sequence(
                    new TempoMap(ppqn, tempos.Select(t => new TempoChange(new Tick(t.Item1), new Tempo(t.Item2)))),
                    new MeterMap(ppqn, meters.Select(m => new MeterChange(new Tick(m.Item1), TimeSignature.FromExponent(m.Item2, m.Item3)))),
                    tracks.Select(t => new Track(TrackId.New(), t.Item1, t.Item2)),
                    []);
            });

    [Fact]
    public void ExportThenImport_PreservesTheSequence() =>
        GenSequence.Sample(original =>
        {
            var bytes = SmfWriter.Write(SmfExporter.Export(original, "Title").File);
            var imported = SmfImporter.Import(SmfReader.Read(bytes).File);
            return imported.Title == "Title"
                && imported.Diagnostics.Count == 0
                && imported.Sequence.TempoMap.Changes.SequenceEqual(original.TempoMap.Changes)
                && imported.Sequence.MeterMap.Changes.SequenceEqual(original.MeterMap.Changes)
                && imported.Sequence.Tracks.Select(Describe).SequenceEqual(original.Tracks.Select(Describe));
        });

    private static string Describe(Track track) =>
        track.Name + ":" + string.Join(";", track.Events.Select(e => e switch
        {
            NoteEvent n => $"N{n.Position}/{n.Duration}/{n.Channel}/{n.Note}/{n.Velocity}/{n.ReleaseVelocity}",
            ChannelEvent c => $"C{c.Position}/{c.Message}",
            SysExEvent s => $"S{s.Position}/{Convert.ToHexString(s.Message.Bytes.Span)}",
            MetaEvent m => $"M{m.Position}/{m.Type}/{Convert.ToHexString(m.Data.Span)}",
            RawMidiEvent r => $"R{r.Position}/{Convert.ToHexString(r.Bytes.Span)}",
            _ => "?",
        }));
}
