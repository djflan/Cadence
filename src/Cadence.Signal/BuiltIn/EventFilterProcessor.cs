using Cadence.Domain.Devices;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;

namespace Cadence.Signal.BuiltIn;

/// <summary>
/// Removes whole classes of events: notes, controllers, program changes, or system exclusive data. It is
/// the explicit way to drop events; no other built-in device removes what it does not understand.
/// </summary>
public sealed class EventFilterProcessor : ISignalProcessor
{
    public static readonly ParameterId NotesParameter = new(1);
    public static readonly ParameterId ControllersParameter = new(2);
    public static readonly ParameterId ProgramsParameter = new(3);
    public static readonly ParameterId SystemExclusiveParameter = new(4);

    public static DeviceDefinition Definition { get; } = new()
    {
        Id = new DeviceDefinitionId("cadence.midi.event-filter"),
        Name = "Event Filter",
        Origin = DeviceOrigin.BuiltIn,
        Vendor = "Cadence",
        Version = "1",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Transmitted,
        Parameters =
        [
            Switch(NotesParameter, "Pass notes"),
            Switch(ControllersParameter, "Pass controllers"),
            Switch(ProgramsParameter, "Pass program changes"),
            Switch(SystemExclusiveParameter, "Pass system exclusive"),
        ],
    };

    private EventClass _blocked;

    /// <summary>An Event Filter device that removes <paramref name="blocked"/>.</summary>
    public static DeviceInstance CreateInstance(EventClass blocked)
    {
        var device = DeviceInstance.Create(Definition.ToReference());
        foreach (var (parameter, eventClass) in Classes)
        {
            device = device.WithParameter(parameter, blocked.HasFlag(eventClass) ? ControlValue.Min : ControlValue.Max);
        }

        return device;
    }

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        foreach (var (id, eventClass) in Classes)
        {
            if (id == parameter)
            {
                _blocked = value.ToFraction() >= 0.5 ? _blocked & ~eventClass : _blocked | eventClass;
            }
        }
    }

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            if ((EventClassification.Of(e.Event) & _blocked) == EventClass.None)
            {
                output.Add(e);
            }
        }
    }

    public void Reset()
    {
    }

    private static (ParameterId Id, EventClass Class)[] Classes { get; } =
    [
        (NotesParameter, EventClass.Notes),
        (ControllersParameter, EventClass.Controllers),
        (ProgramsParameter, EventClass.Programs),
        (SystemExclusiveParameter, EventClass.SystemExclusive),
    ];

    private static ParameterDescriptor Switch(ParameterId id, string name) => new(id, name, 0, 1, 1, steps: 1);
}
