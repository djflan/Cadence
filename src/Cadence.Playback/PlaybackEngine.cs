using System.Collections.Concurrent;
using Cadence.Domain.Midi;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;

namespace Cadence.Playback;

/// <summary>
/// The real-time scheduler and transport. Commands (play, stop, seek, loop, panic, plan and output
/// changes) may be issued from any thread; they are queued and applied by <see cref="Pump"/>, which a
/// single playback thread calls repeatedly (see <see cref="PlaybackThread"/>). Tests drive
/// <see cref="Pump"/> directly with a <see cref="VirtualClock"/>.
/// </summary>
/// <remarks>
/// <para>
/// Messages for endpoints with <see cref="EndpointCapabilities.ScheduledDelivery"/> are handed over up
/// to <see cref="PlaybackOptions.LookAhead"/> early with explicit timestamps; messages for other
/// endpoints are sent when due. Each class has its own cursor through the plan.
/// </para>
/// <para>
/// Note releases are not part of the plan: when a note starts, its release is entered in a fixed-size
/// table. Replacing the plan mid-note, stopping, seeking, or looping therefore always releases every
/// note that sounded, at the right time, even if the note was deleted by an edit.
/// </para>
/// <para>
/// <see cref="Pump"/> does not allocate, lock, log, or throw for endpoint failures. Applying a command
/// may allocate; commands are rare.
/// </para>
/// </remarks>
public sealed class PlaybackEngine : IDisposable
{
    private const int ChannelsPerSlot = 16;
    private const int MaxLoopWrapsPerPump = 10_000;
    private static readonly TimeSpan Never = TimeSpan.MaxValue;

    private readonly IMonotonicClock _clock;
    private readonly PlaybackOptions _options;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _workSignal = new(false);
    private readonly ActiveNote[] _active;
    private int _activeCount;
    private int _pumping;

    // Engine-thread state.
    private PlaybackPlan _plan;
    private IMidiOutput?[] _outputs = [];
    private bool[] _slotScheduled = [];
    private bool[] _sustain = [];
    private LoopRegion? _loop;
    private bool _playing;
    private Cursor _immediate;
    private Cursor _scheduled;

    // Caller-visible state.
    private PlaybackPlan _latestPlan;
    private long _positionTick;
    private int _state;

    public PlaybackEngine(IMonotonicClock clock, TempoMap initialTempo, PlaybackOptions? options = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? PlaybackOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxActiveNotes, nameof(options));
        _active = new ActiveNote[_options.MaxActiveNotes];
        _plan = _latestPlan = PlaybackPlan.Empty(initialTempo);
        Statistics = new TimingStatistics(_options.LateThreshold);
    }

    public TimingStatistics Statistics { get; }

    /// <summary>Signalled when a command is queued, so an idle playback thread can wake.</summary>
    public WaitHandle WorkSignal => _workSignal;

    public TransportState State => (TransportState)Volatile.Read(ref _state);

    /// <summary>The playhead as of the last <see cref="Pump"/>, or the last requested position.</summary>
    public Tick Position => new(Volatile.Read(ref _positionTick));

    public IMonotonicClock Clock => _clock;

    /// <summary>Replaces the plan. During playback, nothing already dispatched is repeated and every sounding note still releases.</summary>
    public void Load(PlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Volatile.Write(ref _latestPlan, plan);
        Enqueue(new LoadCommand(plan));
    }

    /// <summary>Sets the output for each slot used by plan bindings. Sounding notes are released first.</summary>
    public void SetOutputs(IReadOnlyList<IMidiOutput?> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        Enqueue(new OutputsCommand([.. outputs]));
    }

    /// <summary>Starts (or restarts) playback from <paramref name="from"/>, or from <see cref="Position"/>, chasing controller state.</summary>
    public void Play(Tick? from = null)
    {
        var tick = from ?? Position;
        Volatile.Write(ref _positionTick, tick.Value);
        Enqueue(new PlayCommand(tick.Value, ChaseState.Compute(Volatile.Read(ref _latestPlan), tick.Value)));
    }

    /// <summary>Moves the playhead. While playing, sounding notes are released and controller state is chased.</summary>
    public void Seek(Tick position)
    {
        Volatile.Write(ref _positionTick, position.Value);
        Enqueue(new SeekCommand(position.Value, ChaseState.Compute(Volatile.Read(ref _latestPlan), position.Value)));
    }

    /// <summary>Stops playback, releasing sounding notes and any held sustain pedal.</summary>
    public void Stop() => Enqueue(new StopCommand());

    /// <summary>Sets or clears the loop. A loop engages when the playhead is before its end.</summary>
    public void SetLoop(LoopRegion? loop) => Enqueue(new LoopCommand(loop));

    /// <summary>
    /// Releases every sounding note, then sends sustain off, All Notes Off, and All Sound Off on all
    /// sixteen channels of every output. Playback continues if it was running.
    /// </summary>
    public void Panic() => Enqueue(new PanicCommand());

    /// <summary>
    /// Applies queued commands and dispatches everything due. Call from one thread only.
    /// </summary>
    /// <returns>How long until there is more work, or <see cref="Timeout.InfiniteTimeSpan"/> when idle.</returns>
    public TimeSpan Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) != 0)
        {
            throw new InvalidOperationException("Pump must not be called concurrently.");
        }

        try
        {
            var now = _clock.Now;
            while (_commands.TryDequeue(out var command))
            {
                Execute(command, now);
            }

            if (!_playing)
            {
                return Timeout.InfiniteTimeSpan;
            }

            var nextImmediate = Advance(ref _immediate, scheduledClass: false, horizon: now, now);
            var nextScheduled = Advance(ref _scheduled, scheduledClass: true, horizon: now + _options.LookAhead, now);
            Volatile.Write(ref _positionTick, Math.Max(_immediate.AnchorTick, TickAtTime(in _immediate, now)));

            var wake = nextScheduled == Never ? nextImmediate : Min(nextImmediate, nextScheduled - _options.LookAhead);
            var wait = wake == Never ? _options.PositionUpdateInterval : Max(TimeSpan.Zero, wake - now);
            return Min(wait, _options.PositionUpdateInterval);
        }
        finally
        {
            Volatile.Write(ref _pumping, 0);
        }
    }

    public void Dispose() => _workSignal.Dispose();

    private void Enqueue(Command command)
    {
        _commands.Enqueue(command);
        _workSignal.Set();
    }

    private void Execute(Command command, TimeSpan now)
    {
        switch (command)
        {
            case LoadCommand load:
                if (_playing)
                {
                    Reanchor(ref _immediate, load.Plan);
                    Reanchor(ref _scheduled, load.Plan);
                }

                _plan = load.Plan;
                break;
            case OutputsCommand outputs:
                ReleaseAll(now);
                _outputs = outputs.Outputs;
                _slotScheduled = [.. outputs.Outputs.Select(o => o?.Endpoint.Capabilities.HasFlag(EndpointCapabilities.ScheduledDelivery) == true)];
                _sustain = new bool[outputs.Outputs.Length * ChannelsPerSlot];
                break;
            case PlayCommand play:
                Locate(play.Tick, play.Chase, now);
                _playing = true;
                Volatile.Write(ref _state, (int)TransportState.Playing);
                break;
            case SeekCommand seek:
                if (_playing)
                {
                    Locate(seek.Tick, seek.Chase, now);
                }

                break;
            case StopCommand:
                if (_playing)
                {
                    Volatile.Write(ref _positionTick, Math.Max(_immediate.AnchorTick, TickAtTime(in _immediate, now)));
                }

                ReleaseAll(now);
                ReleaseSustain();
                _playing = false;
                Volatile.Write(ref _state, (int)TransportState.Stopped);
                break;
            case LoopCommand loop:
                _loop = loop.Loop;
                break;
            case PanicCommand:
                ReleaseAll(now);
                SweepAllChannels();
                break;
        }
    }

    private void Locate(long tick, ChaseMessage[] chase, TimeSpan now)
    {
        ReleaseAll(now);
        ReleaseSustain();
        foreach (var message in chase)
        {
            SendChannel(message.Slot, message.Message, MidiTimestamp.Immediate);
        }

        _immediate = Cursor.At(_plan, tick, now);
        _scheduled = Cursor.At(_plan, tick, now);
    }

    /// <summary>Moves a cursor onto a new plan at the first tick it has not yet processed.</summary>
    private void Reanchor(ref Cursor cursor, PlaybackPlan next)
    {
        var firstUnprocessed = cursor.ProcessedThrough + 1;
        var time = TimeOf(in cursor, firstUnprocessed);
        cursor = Cursor.At(next, firstUnprocessed, time);
    }

    private TimeSpan Advance(ref Cursor cursor, bool scheduledClass, TimeSpan horizon, TimeSpan now)
    {
        var events = _plan.Events;
        var wraps = 0;
        while (true)
        {
            while (cursor.Index < events.Length && !InClass(events[cursor.Index].Slot, scheduledClass))
            {
                cursor.Index++;
            }

            var loop = _loop;
            var looping = loop is not null && cursor.AnchorTick < loop.End.Value && wraps < MaxLoopWrapsPerPump;
            var wrapTime = looping ? TimeOf(in cursor, loop!.End.Value) : Never;
            if (looping && wrapTime <= cursor.AnchorTime)
            {
                // A loop too short to measure in TimeSpan resolution would never advance time.
                looping = false;
                wrapTime = Never;
            }

            var hasEvent = cursor.Index < events.Length && !(looping && events[cursor.Index].Tick >= loop!.End.Value);
            var eventTime = hasEvent ? TimeOf(in cursor, events[cursor.Index].Tick) : Never;
            var release = NextRelease(scheduledClass, out var releaseTime);

            if (release >= 0 && releaseTime <= horizon && releaseTime <= eventTime && releaseTime <= wrapTime)
            {
                Release(release, now);
                continue;
            }

            if (wrapTime <= horizon && wrapTime <= eventTime)
            {
                ReleaseClass(scheduledClass, wrapTime, now);
                cursor = Cursor.At(_plan, loop!.Start.Value, wrapTime);
                wraps++;
                continue;
            }

            if (eventTime <= horizon)
            {
                Dispatch(in events[cursor.Index], eventTime, in cursor, scheduledClass, now);
                cursor.Index++;
                continue;
            }

            cursor.ProcessedThrough = TickAtTime(in cursor, horizon);
            return Min(eventTime, Min(releaseTime, wrapTime));
        }
    }

    private void Dispatch(in PlanEvent e, TimeSpan due, in Cursor cursor, bool scheduledClass, TimeSpan now)
    {
        var output = _outputs[e.Slot];
        if (output is null)
        {
            return;
        }

        var lateness = now - due;
        var timestamp = scheduledClass ? MidiTimestamp.At(due) : MidiTimestamp.Immediate;
        SendResult result;
        if (e.IsNote)
        {
            if (lateness > _options.MaxNoteLateness)
            {
                Statistics.RecordSkippedLateNote();
                return;
            }

            if (_activeCount == _active.Length)
            {
                Statistics.RecordNoteOverflow();
                return;
            }

            result = Send(output, e.Message, timestamp);
            if (result == SendResult.Sent)
            {
                var releaseTick = e.Tick + e.DurationTicks;
                if (_loop is { } loop && e.Tick < loop.End.Value && releaseTick > loop.End.Value && cursor.AnchorTick < loop.End.Value)
                {
                    releaseTick = loop.End.Value;
                }

                var off = ChannelMessage.NoteOff(e.Message.Channel, e.Message.Note, e.ReleaseVelocity);
                _active[_activeCount++] = new ActiveNote(output, off, due, TimeOf(in cursor, releaseTick), scheduledClass);
            }
        }
        else if (e.PayloadIndex >= 0)
        {
            result = output.Send(_plan.Payloads[e.PayloadIndex].Span, timestamp);
        }
        else
        {
            result = Send(output, e.Message, timestamp);
            TrackSustain(e.Slot, e.Message);
        }

        Record(result, lateness);
    }

    private int NextRelease(bool scheduledClass, out TimeSpan time)
    {
        var best = -1;
        time = Never;
        for (var i = 0; i < _activeCount; i++)
        {
            if (_active[i].Scheduled == scheduledClass && _active[i].ReleaseTime < time)
            {
                best = i;
                time = _active[i].ReleaseTime;
            }
        }

        return best;
    }

    private void Release(int index, TimeSpan now)
    {
        var note = _active[index];
        var timestamp = note.Scheduled ? MidiTimestamp.At(note.ReleaseTime) : MidiTimestamp.Immediate;
        Record(Send(note.Output, note.Off, timestamp), now - note.ReleaseTime);
        RemoveActive(index);
    }

    /// <summary>Releases every note of one class at <paramref name="time"/> (used at loop wrap).</summary>
    private void ReleaseClass(bool scheduledClass, TimeSpan time, TimeSpan now)
    {
        for (var i = _activeCount - 1; i >= 0; i--)
        {
            if (_active[i].Scheduled == scheduledClass)
            {
                _active[i] = _active[i] with { ReleaseTime = Min(_active[i].ReleaseTime, time) };
                Release(i, now);
            }
        }
    }

    /// <summary>
    /// Releases every sounding note now. A note whose start was handed to a scheduling endpoint but has
    /// not yet sounded is released at its start time, so its release can never arrive first.
    /// </summary>
    private void ReleaseAll(TimeSpan now)
    {
        for (var i = _activeCount - 1; i >= 0; i--)
        {
            var note = _active[i];
            var timestamp = note.Scheduled && note.StartTime > now ? MidiTimestamp.At(note.StartTime) : MidiTimestamp.Immediate;
            Record(Send(note.Output, note.Off, timestamp), TimeSpan.Zero);
            RemoveActive(i);
        }
    }

    private void RemoveActive(int index)
    {
        _active[index] = _active[--_activeCount];
        _active[_activeCount] = default;
    }

    private void TrackSustain(int slot, ChannelMessage message)
    {
        if (message.Kind == ChannelMessageKind.ControlChange && message.Data1 == ControllerNumber.SustainPedal.Value)
        {
            _sustain[(slot * ChannelsPerSlot) + message.Channel.Index] = message.Data2 >= 64;
        }
    }

    private void ReleaseSustain()
    {
        for (var i = 0; i < _sustain.Length; i++)
        {
            if (_sustain[i])
            {
                _sustain[i] = false;
                var off = ChannelMessage.ControlChange(MidiChannel.FromIndex(i % ChannelsPerSlot), ControllerNumber.SustainPedal, SevenBitValue.Min);
                SendChannel(i / ChannelsPerSlot, off, MidiTimestamp.Immediate);
            }
        }
    }

    private void SweepAllChannels()
    {
        Array.Clear(_sustain);
        for (var slot = 0; slot < _outputs.Length; slot++)
        {
            for (var channel = 0; channel < ChannelsPerSlot; channel++)
            {
                var ch = MidiChannel.FromIndex(channel);
                SendChannel(slot, ChannelMessage.ControlChange(ch, ControllerNumber.SustainPedal, SevenBitValue.Min), MidiTimestamp.Immediate);
                SendChannel(slot, ChannelMessage.ControlChange(ch, ControllerNumber.AllNotesOff, SevenBitValue.Min), MidiTimestamp.Immediate);
                SendChannel(slot, ChannelMessage.ControlChange(ch, ControllerNumber.AllSoundOff, SevenBitValue.Min), MidiTimestamp.Immediate);
            }
        }
    }

    private void SendChannel(int slot, ChannelMessage message, MidiTimestamp timestamp)
    {
        if ((uint)slot < (uint)_outputs.Length && _outputs[slot] is { } output)
        {
            Record(Send(output, message, timestamp), TimeSpan.Zero);
        }
    }

    private static SendResult Send(IMidiOutput output, ChannelMessage message, MidiTimestamp timestamp)
    {
        Span<byte> buffer = stackalloc byte[3];
        var length = message.CopyTo(buffer);
        return output.Send(buffer[..length], timestamp);
    }

    private void Record(SendResult result, TimeSpan lateness)
    {
        if (result == SendResult.Sent)
        {
            Statistics.RecordDispatch(lateness);
        }
        else
        {
            Statistics.RecordDropped();
        }
    }

    private bool InClass(int slot, bool scheduledClass) =>
        (uint)slot < (uint)_outputs.Length && _outputs[slot] is not null && _slotScheduled[slot] == scheduledClass;

    private TimeSpan TimeOf(in Cursor cursor, long tick) =>
        cursor.AnchorTime + (_plan.TempoMap.TimeAt(new Tick(tick)) - cursor.AnchorSongTime);

    /// <summary>The last tick due at or before <paramref name="time"/>, or one before the anchor if none.</summary>
    private long TickAtTime(in Cursor cursor, TimeSpan time) =>
        time < cursor.AnchorTime
            ? cursor.AnchorTick - 1
            : _plan.TempoMap.TickAt(cursor.AnchorSongTime + (time - cursor.AnchorTime)).Value;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;

    /// <summary>A position in the plan, and the mapping from ticks to clock time for the current pass.</summary>
    private struct Cursor
    {
        public int Index;
        public long AnchorTick;
        public TimeSpan AnchorTime;
        public TimeSpan AnchorSongTime;
        public long ProcessedThrough;

        public static Cursor At(PlaybackPlan plan, long tick, TimeSpan time) => new()
        {
            Index = plan.FirstIndexAtOrAfter(tick),
            AnchorTick = tick,
            AnchorTime = time,
            AnchorSongTime = plan.TempoMap.TimeAt(new Tick(tick)),
            ProcessedThrough = tick - 1,
        };
    }

    private readonly record struct ActiveNote(IMidiOutput Output, ChannelMessage Off, TimeSpan StartTime, TimeSpan ReleaseTime, bool Scheduled);

    private abstract record Command;

    private sealed record LoadCommand(PlaybackPlan Plan) : Command;

    private sealed record OutputsCommand(IMidiOutput?[] Outputs) : Command;

    private sealed record PlayCommand(long Tick, ChaseMessage[] Chase) : Command;

    private sealed record SeekCommand(long Tick, ChaseMessage[] Chase) : Command;

    private sealed record StopCommand : Command;

    private sealed record LoopCommand(LoopRegion? Loop) : Command;

    private sealed record PanicCommand : Command;
}
