using Cadence.Domain.Midi;
using Cadence.Domain.Time;

namespace Cadence.Domain.Sequencing;

/// <summary>An event placed at a tick on a track. Concrete events are immutable records.</summary>
public abstract record TrackEvent
{
    protected TrackEvent(EventId id, Tick position)
    {
        Id = id;
        Position = position;
    }

    public EventId Id { get; init; }

    public Tick Position { get; init; }

    /// <summary>Where this event falls among others at the same tick.</summary>
    public abstract EventPhase Phase { get; }

    /// <summary>The last tick this event affects; for notes, the release position.</summary>
    public virtual Tick EndPosition => Position;
}

/// <summary>
/// A paired note with a duration. Duration is at least one tick so a note's release can never be
/// ordered before its own start.
/// </summary>
public sealed record NoteEvent : TrackEvent
{
    private readonly TickSpan _duration;
    private readonly Velocity _velocity;

    public NoteEvent(EventId id, Tick position, TickSpan duration, MidiChannel channel, NoteNumber note, Velocity velocity, Velocity releaseVelocity)
        : base(id, position)
    {
        Duration = duration;
        Channel = channel;
        Note = note;
        Velocity = velocity;
        ReleaseVelocity = releaseVelocity;
    }

    public NoteEvent(Tick position, TickSpan duration, MidiChannel channel, NoteNumber note, Velocity velocity)
        : this(EventId.New(), position, duration, channel, note, velocity, Velocity.DefaultRelease)
    {
    }

    public TickSpan Duration
    {
        get => _duration;
        init => _duration = value.Value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Duration), value, "A note must last at least one tick.");
    }

    public MidiChannel Channel { get; init; }

    public NoteNumber Note { get; init; }

    /// <summary>Attack velocity, 1-127. Velocity 0 would be read as a release on the wire.</summary>
    public Velocity Velocity
    {
        get => _velocity;
        init => _velocity = value.IsAudible
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Velocity), value, "A note's velocity must be 1-127.");
    }

    public Velocity ReleaseVelocity { get; init; }

    public override Tick EndPosition => Position + Duration;

    public override EventPhase Phase => EventPhase.NoteOn;
}

/// <summary>
/// A channel voice message other than a paired note: controllers, program changes, pitch bend,
/// pressure, and any note-on/off that could not be paired on import (kept for fidelity).
/// </summary>
public sealed record ChannelEvent : TrackEvent
{
    public ChannelEvent(EventId id, Tick position, ChannelMessage message)
        : base(id, position) => Message = message;

    public ChannelEvent(Tick position, ChannelMessage message)
        : this(EventId.New(), position, message)
    {
    }

    public ChannelMessage Message { get; init; }

    public override EventPhase Phase => EventOrder.PhaseOf(Message);
}

/// <summary>A complete System Exclusive message.</summary>
public sealed record SysExEvent : TrackEvent
{
    public SysExEvent(EventId id, Tick position, SysExMessage message)
        : base(id, position) => Message = message ?? throw new ArgumentNullException(nameof(message));

    public SysExEvent(Tick position, SysExMessage message)
        : this(EventId.New(), position, message)
    {
    }

    public SysExMessage Message { get; init; }

    public override EventPhase Phase => EventPhase.SystemExclusive;
}

/// <summary>
/// Bytes transmitted verbatim, such as a Standard MIDI File "escape" (<c>F7</c>) event or a SysEx
/// packet that was split across several events. Preserved for round-trip fidelity; not interpreted.
/// </summary>
public sealed record RawMidiEvent : TrackEvent
{
    public const int MaxLength = SysExMessage.MaxLength;

    public RawMidiEvent(EventId id, Tick position, ByteBlock bytes)
        : base(id, position)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Bytes = bytes.Length <= MaxLength
            ? bytes
            : throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length, "Raw event payload is too large.");
    }

    public ByteBlock Bytes { get; init; }

    public override EventPhase Phase => EventPhase.SystemExclusive;
}

/// <summary>
/// A meta event that Cadence does not model directly (text, lyrics, cue points, sequencer-specific
/// data, or unknown types), preserved for round-trip fidelity. Tempo and time signature live in the
/// sequence's <see cref="TempoMap"/> and <see cref="MeterMap"/>, not here.
/// </summary>
public sealed record MetaEvent : TrackEvent
{
    public const int MaxLength = SysExMessage.MaxLength;

    public MetaEvent(EventId id, Tick position, byte type, ByteBlock data)
        : base(id, position)
    {
        ArgumentNullException.ThrowIfNull(data);
        Type = type <= 0x7F ? type : throw new ArgumentOutOfRangeException(nameof(type), type, "Meta event types are 0-127.");
        Data = data.Length <= MaxLength
            ? data
            : throw new ArgumentOutOfRangeException(nameof(data), data.Length, "Meta event payload is too large.");
    }

    public byte Type { get; init; }

    public ByteBlock Data { get; init; }

    public override EventPhase Phase => EventPhase.Meta;
}
