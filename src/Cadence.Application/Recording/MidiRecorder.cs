using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Projects;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Playback;

namespace Cadence.Application.Recording;

/// <summary>Where played notes are echoed while a track is selected or armed.</summary>
/// <param name="Output">The track's open output.</param>
/// <param name="Channel">The route's channel override, if any.</param>
/// <param name="Transpose">The route's transposition, applied as playback would.</param>
public sealed record ThruTarget(IMidiOutput Output, MidiChannel? Channel, int Transpose);

/// <summary>A note in a take, for drawing it while recording. <see cref="IsHeld"/> notes are still sounding.</summary>
public readonly record struct RecordingNote(long Start, long End, NoteNumber Note, Velocity Velocity, bool IsHeld);

/// <summary>A finished take: the events played and the range that was recorded over.</summary>
public sealed record RecordedTake(TrackId Track, ImmutableArray<TrackEvent> Events, TickRange? Range);

/// <summary>
/// Receives MIDI input, echoes it to the selected track's output (MIDI thru), and captures it into a
/// take while recording. Messages are timestamped by the adapter and placed on the timeline with
/// <see cref="PlaybackEngine.TryGetTickAt"/>, so input latency in the UI never moves notes.
/// </summary>
/// <remarks>
/// Input handlers run on adapter threads. They only enqueue thru messages for the playback thread
/// and append to the take under a short lock; pairing notes into events happens when recording stops.
/// System messages (SysEx, clock, active sensing) are neither echoed nor recorded.
/// </remarks>
public sealed class MidiRecorder : IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly Lock _gate = new();
    private readonly List<Captured> _captured = [];
    private readonly Dictionary<(int Input, byte Channel, byte Note), (IMidiOutput Output, ChannelMessage Off)> _thruHeld = [];
    private readonly List<OpenInput> _inputs = [];
    private ThruTarget? _thru;
    private TrackId _track;
    private long _start;
    private volatile bool _recording;
    private long _received;
    private int _nextInputKey;

    public MidiRecorder(PlaybackEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public bool IsRecording => _recording;

    /// <summary>The track the current take is recorded into.</summary>
    public TrackId? RecordingTrack => _recording ? _track : null;

    /// <summary>Channel messages received from any input since the recorder was created.</summary>
    public long MessagesReceived => Interlocked.Read(ref _received);

    /// <summary>The inputs currently open.</summary>
    public IReadOnlyList<EndpointDescriptor> Inputs
    {
        get
        {
            lock (_gate)
            {
                return [.. _inputs.Select(i => i.Input.Endpoint)];
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="wanted"/> and closes any other open inputs. Inputs that are already open
    /// stay open, so a note held across the change still releases.
    /// </summary>
    /// <returns>Problems opening inputs, for display.</returns>
    public async Task<ImmutableArray<string>> SetInputsAsync(EndpointDirectory directory, IReadOnlyCollection<EndpointId> wanted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(wanted);
        var problems = ImmutableArray.CreateBuilder<string>();
        List<OpenInput> stale;
        HashSet<EndpointId> open;
        lock (_gate)
        {
            stale = [.. _inputs.Where(i => !wanted.Contains(i.Input.Endpoint.Id) || i.Input.State != EndpointState.Open)];
            _inputs.RemoveAll(stale.Contains);
            open = [.. _inputs.Select(i => i.Input.Endpoint.Id)];
        }

        foreach (var input in stale)
        {
            input.Input.SetReceiver(null);
            input.Input.Dispose();
            ReleaseThru(input.Key);
        }

        foreach (var id in wanted.Where(id => !open.Contains(id)))
        {
            try
            {
                var input = await directory.OpenInputAsync(id, cancellationToken).ConfigureAwait(false);
                var entry = new OpenInput(input, Interlocked.Increment(ref _nextInputKey));
                lock (_gate)
                {
                    _inputs.Add(entry);
                }

                input.SetReceiver((message, timestamp) => Receive(entry, message, timestamp));
            }
            catch (EndpointUnavailableException ex)
            {
                problems.Add(ex.Message);
            }
        }

        return problems.ToImmutable();
    }

    /// <summary>Sets where input is echoed, or <see langword="null"/> to stop echoing. Held notes still release where they started.</summary>
    public void SetThru(ThruTarget? target) => Volatile.Write(ref _thru, target);

    /// <summary>Starts a take on <paramref name="track"/>. Input from now on is captured until <see cref="Stop"/>.</summary>
    public void Start(TrackId track, Tick from)
    {
        lock (_gate)
        {
            _captured.Clear();
            _track = track;
            _start = from.Value;
        }

        _recording = true;
    }

    /// <summary>
    /// Ends the take and pairs notes. Notes still held end at <paramref name="end"/>; in a loop, notes
    /// held across the wrap end at the loop's end.
    /// </summary>
    /// <param name="end">The song position when recording stopped.</param>
    /// <param name="loop">The loop that was active while recording, if any.</param>
    public RecordedTake Stop(Tick end, TickRange? loop)
    {
        _recording = false;
        List<Captured> captured;
        TrackId track;
        long start;
        lock (_gate)
        {
            captured = [.. _captured];
            _captured.Clear();
            track = _track;
            start = _start;
        }

        var events = Build(captured, end.Value, loop, held: false).Events;
        TickRange? range = loop ?? (end.Value > start ? new TickRange(new Tick(start), end) : null);
        return new RecordedTake(track, events, range);
    }

    /// <summary>The notes captured so far, with held notes ending at <paramref name="now"/>, for drawing while recording.</summary>
    public IReadOnlyList<RecordingNote> Preview(Tick now, TickRange? loop)
    {
        if (!_recording)
        {
            return [];
        }

        List<Captured> captured;
        lock (_gate)
        {
            captured = [.. _captured];
        }

        return Build(captured, now.Value, loop, held: true).Preview;
    }

    public void Dispose()
    {
        _recording = false;
        List<OpenInput> inputs;
        lock (_gate)
        {
            inputs = [.. _inputs];
            _inputs.Clear();
        }

        foreach (var input in inputs)
        {
            input.Input.SetReceiver(null);
            input.Input.Dispose();
            ReleaseThru(input.Key);
        }
    }

    private void Receive(OpenInput input, ReadOnlySpan<byte> bytes, TimeSpan timestamp)
    {
        if (bytes.Length < 2 || bytes[0] < 0x80 || bytes[0] >= 0xF0
            || !ChannelMessage.TryCreate(bytes[0], bytes[1], bytes.Length > 2 ? bytes[2] : (byte)0, out var message))
        {
            return;
        }

        Interlocked.Increment(ref _received);
        Echo(input, message);
        if (_recording && _engine.TryGetTickAt(timestamp, out var tick))
        {
            lock (_gate)
            {
                if (_recording)
                {
                    _captured.Add(new Captured(tick.Value, _captured.Count, message));
                }
            }
        }
    }

    private void Echo(OpenInput input, ChannelMessage message)
    {
        var key = (input.Key, message.Channel.Index, message.Data1);
        if (message.IsNoteOff)
        {
            // A release goes wherever its note went, even if the thru target changed in between.
            (IMidiOutput Output, ChannelMessage Off) held;
            bool found;
            lock (_gate)
            {
                found = _thruHeld.Remove(key, out held);
            }

            if (found)
            {
                _engine.SendNow(held.Output, held.Off);
            }

            return;
        }

        if (Volatile.Read(ref _thru) is not { } target || IsFeedback(input.Input.Endpoint, target.Output.Endpoint))
        {
            return;
        }

        var echoed = target.Channel is { } channel ? message.WithChannel(channel) : message;
        if (target.Transpose != 0 && message.Kind is ChannelMessageKind.NoteOn or ChannelMessageKind.PolyPressure)
        {
            if (!message.Note.TryTranspose(target.Transpose, out var note))
            {
                return;
            }

            ChannelMessage.TryCreate(echoed.Status, note.Value, echoed.Data2, out echoed);
        }

        if (echoed.IsNoteOn)
        {
            var off = ChannelMessage.NoteOff(echoed.Channel, echoed.Note, Velocity.DefaultRelease);
            lock (_gate)
            {
                _thruHeld[key] = (target.Output, off);
            }
        }

        _engine.SendNow(target.Output, echoed);
    }

    /// <summary>
    /// True when echoing would send input straight back to where it came from, such as an IAC bus used
    /// both ways, which would loop forever.
    /// </summary>
    private static bool IsFeedback(EndpointDescriptor input, EndpointDescriptor output) =>
        input.Id.Provider == output.Id.Provider && string.Equals(input.DisplayName, output.DisplayName, StringComparison.Ordinal);

    private void ReleaseThru(int inputKey)
    {
        List<(IMidiOutput Output, ChannelMessage Off)> released;
        lock (_gate)
        {
            var keys = _thruHeld.Keys.Where(k => k.Input == inputKey).ToList();
            released = [.. keys.Select(k => _thruHeld[k])];
            foreach (var k in keys)
            {
                _thruHeld.Remove(k);
            }
        }

        foreach (var (output, off) in released)
        {
            _engine.SendNow(output, off);
        }
    }

    private static (ImmutableArray<TrackEvent> Events, List<RecordingNote> Preview) Build(List<Captured> captured, long end, TickRange? loop, bool held)
    {
        captured.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));
        var events = ImmutableArray.CreateBuilder<TrackEvent>();
        var preview = new List<RecordingNote>();
        var open = new Dictionary<(byte Channel, byte Note), Queue<Captured>>();
        var loopEnd = loop?.End.Value;

        foreach (var c in captured)
        {
            var message = c.Message;
            var key = (message.Channel.Index, message.Data1);
            if (message.IsNoteOn)
            {
                if (!open.TryGetValue(key, out var queue))
                {
                    open[key] = queue = new Queue<Captured>();
                }

                queue.Enqueue(c);
            }
            else if (message.IsNoteOff)
            {
                if (open.TryGetValue(key, out var queue) && queue.TryDequeue(out var on))
                {
                    var release = message.Kind == ChannelMessageKind.NoteOff ? new Velocity(message.Data2) : Velocity.DefaultRelease;
                    AddNote(on, EndOf(on.Tick, c.Tick), release, isHeld: false);
                }
            }
            else if (!held)
            {
                events.Add(new ChannelEvent(new Tick(c.Tick), message));
            }
        }

        foreach (var on in open.Values.SelectMany(q => q).OrderBy(c => c.Tick).ThenBy(c => c.Order))
        {
            AddNote(on, EndOf(on.Tick, end), Velocity.DefaultRelease, isHeld: true);
        }

        return (events.ToImmutable(), preview);

        // A release that maps before its note's start was played after a loop wrap.
        long EndOf(long start, long release) =>
            release > start ? release : loopEnd is { } wrap && wrap > start ? wrap : start + 1;

        void AddNote(Captured on, long noteEnd, Velocity release, bool isHeld)
        {
            var message = on.Message;
            if (held)
            {
                preview.Add(new RecordingNote(on.Tick, noteEnd, message.Note, message.Velocity, isHeld));
                return;
            }

            events.Add(new NoteEvent(EventId.New(), new Tick(on.Tick), new TickSpan(noteEnd - on.Tick), message.Channel, message.Note, message.Velocity, release));
        }
    }

    private readonly record struct Captured(long Tick, int Order, ChannelMessage Message);

    private sealed record OpenInput(IMidiInput Input, int Key);
}
