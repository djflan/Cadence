using Cadence.Domain.Midi;
using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Playback;

namespace Cadence.Benchmarks;

/// <summary>Deterministic synthetic material for measurements.</summary>
internal static class Workloads
{
    public static readonly Ppqn Resolution = new(960);

    /// <summary>A dense sequence: <paramref name="tracks"/> tracks of sixteenth notes plus controller automation.</summary>
    public static Sequence Dense(int tracks, int notesPerTrack)
    {
        var random = new Random(1234);
        var sixteenth = Resolution.TicksPerQuarterNote / 4;
        var sequence = Sequence.CreateEmpty(Resolution);
        for (var t = 0; t < tracks; t++)
        {
            var channel = MidiChannel.FromIndex(t % 16);
            var events = new List<TrackEvent>(notesPerTrack * 2);
            for (var i = 0; i < notesPerTrack; i++)
            {
                var tick = new Tick((long)i * sixteenth);
                events.Add(new NoteEvent(EventId.New(), tick, new TickSpan(sixteenth - 10), channel, new NoteNumber(36 + random.Next(48)), new Velocity(40 + random.Next(87)), Velocity.DefaultRelease));
                events.Add(new ControllerEvent(tick, channel, ControllerNumber.ModulationWheel, ControlValue.FromSevenBit(random.Next(128))));
            }

            sequence = sequence.WithTrack(new Track(TrackId.New(), $"Track {t + 1}", events));
        }

        return sequence;
    }

    public static Dictionary<TrackId, PlanTrackBinding> Bindings(Sequence sequence) =>
        sequence.Tracks.ToDictionary(t => t.Id, _ => new PlanTrackBinding(0));

    public static Project ProjectFor(Sequence sequence) => Project.CreateNew("Benchmark") with { Sequence = sequence };
}

/// <summary>An output that accepts everything and only counts, so measurements see Cadence's own cost.</summary>
internal sealed class CountingOutput(EndpointCapabilities capabilities) : IMidiOutput
{
    public long Count;

    public EndpointDescriptor Endpoint { get; } = new(new EndpointId("bench", "sink"), "Sink", EndpointDirection.Output, EndpointTransport.Test, capabilities);

    public EndpointState State => EndpointState.Open;

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        Count++;
        return SendResult.Sent;
    }

    public void Dispose()
    {
    }
}
