using System.Collections.ObjectModel;
using System.Globalization;
using Cadence.Application.Editing;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadence.Presentation;

/// <summary>Which events the list shows.</summary>
public sealed record EventFilter(string Name, Func<TrackEvent, bool> Includes)
{
    public static readonly IReadOnlyList<EventFilter> All =
    [
        new("All Events", _ => true),
        new("Notes", e => e is NoteEvent),
        new("Controllers", e => e is ChannelEvent { Message.Kind: ChannelMessageKind.ControlChange }),
        new("Program Changes", e => e is ChannelEvent { Message.Kind: ChannelMessageKind.ProgramChange }),
        new("Pitch Bend & Pressure", e => e is ChannelEvent { Message.Kind: ChannelMessageKind.PitchBend or ChannelMessageKind.ChannelPressure or ChannelMessageKind.PolyPressure }),
        new("SysEx & Meta", e => e is SysExEvent or RawMidiEvent or MetaEvent),
    ];

    public override string ToString() => Name;
}

/// <summary>
/// The event list editor: every event on the edited track as a row, with editable position, channel,
/// data, and length. Shares its selection with the piano roll.
/// </summary>
public sealed partial class EventListViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private readonly EditorViewModel _editor;
    private bool _syncingSelection;

    internal EventListViewModel(MainViewModel owner, EditorViewModel editor)
    {
        _owner = owner;
        _editor = editor;
        _editor.Changed += (_, _) => Rebuild();
    }

    public ObservableCollection<EventRow> Rows { get; } = [];

    /// <summary>Rows selected in the list, kept in step with the editor's selection.</summary>
    public ObservableCollection<EventRow> SelectedRows { get; } = [];

    [ObservableProperty]
    public partial EventFilter Filter { get; set; } = EventFilter.All[0];

    partial void OnFilterChanged(EventFilter value) => Rebuild();

    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>Called by the view when the user changes the list's selection.</summary>
    public void OnRowsSelected()
    {
        if (!_syncingSelection)
        {
            _editor.Select(SelectedRows.Select(r => r.Id));
        }
    }

    internal Sequence Sequence => _owner.Project.Sequence;

    internal void Commit(TrackEvent original, TrackEvent edited, string label)
    {
        if (_editor.Track is { } track && edited != original)
        {
            _owner.Execute(ProjectCommands.ReplaceEvents(track.Id, label, [edited]));
        }
    }

    private void Rebuild()
    {
        var events = _editor.Track?.Events.Where(Filter.Includes).ToList() ?? [];
        var existing = Rows.ToDictionary(r => r.Id);

        // Reuse rows whose event is unchanged so the list keeps its scroll position and focus.
        var rows = events.Select(e => existing.TryGetValue(e.Id, out var row) && ReferenceEquals(row.Event, e) ? row : new EventRow(this, e)).ToList();
        if (!rows.SequenceEqual(Rows))
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }
        }

        _syncingSelection = true;
        try
        {
            var selected = _editor.SelectedEvents;
            for (var i = SelectedRows.Count - 1; i >= 0; i--)
            {
                if (!selected.Contains(SelectedRows[i].Id) || !Rows.Contains(SelectedRows[i]))
                {
                    SelectedRows.RemoveAt(i);
                }
            }

            foreach (var row in Rows.Where(r => selected.Contains(r.Id) && !SelectedRows.Contains(r)))
            {
                SelectedRows.Add(row);
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        CountText = string.Create(CultureInfo.InvariantCulture, $"{Rows.Count} event{(Rows.Count == 1 ? string.Empty : "s")}");
    }
}

/// <summary>
/// One event in the list. Editing a cell parses the text and replaces the event (one undo step);
/// text that does not parse is ignored and the cell shows the event's value again.
/// </summary>
public sealed partial class EventRow : ObservableObject
{
    private readonly EventListViewModel _owner;

    internal EventRow(EventListViewModel owner, TrackEvent e)
    {
        _owner = owner;
        Event = e;
        var meter = owner.Sequence.MeterMap;
        Position = Formatting.Position(meter.ToBarBeatTick(e.Position));
        (Kind, Channel, Data1, Data2, Length) = Describe(e, meter);
        CanEditData = e is NoteEvent or ChannelEvent;
        CanEditLength = e is NoteEvent;
    }

    public TrackEvent Event { get; }

    public EventId Id => Event.Id;

    public string Kind { get; }

    public bool CanEditData { get; }

    public bool CanEditLength { get; }

    [ObservableProperty]
    public partial string Position { get; set; }

    [ObservableProperty]
    public partial string Channel { get; set; }

    /// <summary>Note name, controller number, or program.</summary>
    [ObservableProperty]
    public partial string Data1 { get; set; }

    /// <summary>Velocity or value.</summary>
    [ObservableProperty]
    public partial string Data2 { get; set; }

    [ObservableProperty]
    public partial string Length { get; set; }

    partial void OnPositionChanged(string value)
    {
        if (Formatting.TryParsePosition(value, _owner.Sequence.MeterMap, out var tick))
        {
            _owner.Commit(Event, Event with { Position = tick }, "Move Event");
        }
    }

    partial void OnChannelChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 16
            && EventEdits.SetChannel([Event], MidiChannel.FromNumber(number)) is [var edited])
        {
            _owner.Commit(Event, edited, "Change Channel");
        }
    }

    partial void OnData1Changed(string value)
    {
        switch (Event)
        {
            case NoteEvent note when Formatting.TryParseNote(value, out var pitch):
                _owner.Commit(Event, note with { Note = pitch }, "Change Pitch");
                break;
            case ChannelEvent c when TryByte(value, out var data) && ChannelMessage.TryCreate(c.Message.Status, (byte)data, c.Message.Data2, out var message):
                _owner.Commit(Event, c with { Message = message }, "Edit Event");
                break;
        }
    }

    partial void OnData2Changed(string value)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return;
        }

        switch (Event)
        {
            case NoteEvent note when number is >= 1 and <= 127:
                _owner.Commit(Event, note with { Velocity = new Velocity(number) }, "Change Velocity");
                break;
            case ChannelEvent { Message.Kind: ChannelMessageKind.PitchBend } c when number is >= -8192 and <= 8191:
                _owner.Commit(Event, c with { Message = ChannelMessage.PitchBend(c.Message.Channel, FourteenBitValue.FromOffsetFromCenter(number)) }, "Edit Event");
                break;
            case ChannelEvent c when number is >= 0 and <= 127 && ChannelMessage.DataLength(c.Message.Status) == 2
                && ChannelMessage.TryCreate(c.Message.Status, c.Message.Data1, (byte)number, out var message):
                _owner.Commit(Event, c with { Message = message }, "Edit Event");
                break;
        }
    }

    partial void OnLengthChanged(string value)
    {
        if (Event is NoteEvent note && Formatting.TryParseLength(value, _owner.Sequence.MeterMap, note.Position, out var length))
        {
            _owner.Commit(Event, note with { Duration = length }, "Change Length");
        }
    }

    private static bool TryByte(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 127;

    private static (string Kind, string Channel, string Data1, string Data2, string Length) Describe(TrackEvent e, MeterMap meter)
    {
        static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        return e switch
        {
            NoteEvent n => ("Note", Number(n.Channel.Number), Formatting.NoteName(n.Note), Number(n.Velocity.Value), Formatting.Length(n.Duration, meter, n.Position)),
            ChannelEvent c => c.Message.Kind switch
            {
                ChannelMessageKind.ControlChange => ("Control", Number(c.Message.Channel.Number), Number(c.Message.Data1), Number(c.Message.Data2), string.Empty),
                ChannelMessageKind.ProgramChange => ("Program", Number(c.Message.Channel.Number), Number(c.Message.Data1), string.Empty, string.Empty),
                ChannelMessageKind.PitchBend => ("Pitch Bend", Number(c.Message.Channel.Number), string.Empty, Number(c.Message.PitchBendValue.OffsetFromCenter), string.Empty),
                ChannelMessageKind.ChannelPressure => ("Pressure", Number(c.Message.Channel.Number), Number(c.Message.Data1), string.Empty, string.Empty),
                ChannelMessageKind.PolyPressure => ("Poly Pressure", Number(c.Message.Channel.Number), Formatting.NoteName(c.Message.Note), Number(c.Message.Data2), string.Empty),
                _ when c.Message.IsNoteOn => ("Note On", Number(c.Message.Channel.Number), Formatting.NoteName(c.Message.Note), Number(c.Message.Data2), string.Empty),
                _ => ("Note Off", Number(c.Message.Channel.Number), Formatting.NoteName(c.Message.Note), Number(c.Message.Data2), string.Empty),
            },
            SysExEvent s => ("SysEx", string.Empty, string.Create(CultureInfo.InvariantCulture, $"{s.Message.Bytes.Length} bytes"), string.Empty, string.Empty),
            RawMidiEvent r => ("Raw MIDI", string.Empty, string.Create(CultureInfo.InvariantCulture, $"{r.Bytes.Length} bytes"), string.Empty, string.Empty),
            MetaEvent m => ("Meta", string.Empty, string.Create(CultureInfo.InvariantCulture, $"type {m.Type:X2}"), string.Empty, string.Empty),
            _ => ("Event", string.Empty, string.Empty, string.Empty, string.Empty),
        };
    }
}
