using System.Collections.Immutable;
using Cadence.Domain.Devices;
using Cadence.Domain.Time;
using Cadence.Signal.BuiltIn;

namespace Cadence.Signal;

/// <summary>Creates the processor for one device instance, at the sequence's resolution.</summary>
public delegate ISignalProcessor SignalProcessorFactory(Ppqn ppqn);

/// <summary>
/// The device definitions Cadence knows about, and, for built-in devices, how to run them. Plugin
/// definitions (from a scan) are data only: a plugin never gets an in-process factory, because plugins
/// run in worker processes (ADR 0025). Immutable; <see cref="With"/> returns a new catalog.
/// </summary>
public sealed class DeviceCatalog
{
    private readonly ImmutableDictionary<DeviceDefinitionId, DeviceDefinition> _definitions;
    private readonly ImmutableDictionary<DeviceDefinitionId, SignalProcessorFactory> _factories;

    private DeviceCatalog(ImmutableDictionary<DeviceDefinitionId, DeviceDefinition> definitions, ImmutableDictionary<DeviceDefinitionId, SignalProcessorFactory> factories)
    {
        _definitions = definitions;
        _factories = factories;
    }

    public static DeviceCatalog Empty { get; } = new(ImmutableDictionary<DeviceDefinitionId, DeviceDefinition>.Empty, ImmutableDictionary<DeviceDefinitionId, SignalProcessorFactory>.Empty);

    /// <summary>The devices that ship with Cadence: Transpose, Event Filter, and Arpeggiator.</summary>
    public static DeviceCatalog BuiltIn { get; } = Empty
        .With(BuiltInDevices.Transpose, _ => new TransposeProcessor())
        .With(BuiltInDevices.EventFilter, _ => new EventFilterProcessor())
        .With(BuiltInDevices.Arpeggiator, ppqn => new ArpeggiatorProcessor(ppqn));

    /// <summary>Every definition, in ID order.</summary>
    public ImmutableArray<DeviceDefinition> Definitions => [.. _definitions.Values.OrderBy(d => d.Id.Value, StringComparer.Ordinal)];

    /// <summary>Adds or replaces a definition and, for a built-in device, the factory that runs it.</summary>
    /// <exception cref="ArgumentException">A factory was given for a plugin definition.</exception>
    public DeviceCatalog With(DeviceDefinition definition, SignalProcessorFactory? factory = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (factory is not null && definition.Origin != DeviceOrigin.BuiltIn)
        {
            throw new ArgumentException($"{definition.Name} is a plugin; plugins run in worker processes, never in Cadence's own process.", nameof(factory));
        }

        var factories = factory is null ? _factories.Remove(definition.Id) : _factories.SetItem(definition.Id, factory);
        return new DeviceCatalog(_definitions.SetItem(definition.Id, definition), factories);
    }

    public DeviceDefinition? Find(DeviceDefinitionId id) => _definitions.GetValueOrDefault(id);

    /// <summary>This catalog as the lookup the domain's routing checks take.</summary>
    public DeviceDefinitionLookup Lookup => Find;

    public bool CanProcess(DeviceDefinitionId id) => _factories.ContainsKey(id);

    /// <summary>A new processor for one device instance, or null when the device has no in-process implementation.</summary>
    public ISignalProcessor? CreateProcessor(DeviceDefinitionId id, Ppqn ppqn) =>
        _factories.TryGetValue(id, out var factory) ? factory(ppqn) : null;
}
