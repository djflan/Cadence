using System.Globalization;
using Cadence.Domain.Devices;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;

namespace Cadence.Signal.BuiltIn;

/// <summary>
/// Moves notes by a number of semitones. Notes that would leave 0-127 are dropped and reported, as the
/// old per-track transpose did. Everything else passes around it (it handles only notes).
/// </summary>
public sealed class TransposeProcessor : ISignalProcessor, IReportingProcessor
{
    public const int MaxSemitones = 48;

    public static readonly ParameterId SemitonesParameter = new(1);

    public static DeviceDefinition Definition { get; } = new()
    {
        Id = new DeviceDefinitionId("cadence.midi.transpose"),
        Name = "Transpose",
        Origin = DeviceOrigin.BuiltIn,
        Vendor = "Cadence",
        Version = "1",
        Consumes = SignalKinds.Events,
        Produces = SignalKinds.Events,
        Handles = EventClass.Notes,
        Parameters = [new ParameterDescriptor(SemitonesParameter, "Semitones", -MaxSemitones, MaxSemitones, 0, steps: 2 * MaxSemitones, unit: "st")],
    };

    private static readonly ParameterDescriptor Semitones = Definition.Parameters[0];

    private int _semitones;
    private int _dropped;

    /// <summary>A Transpose device set to <paramref name="semitones"/>.</summary>
    public static DeviceInstance CreateInstance(int semitones)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(semitones, -MaxSemitones);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(semitones, MaxSemitones);
        return DeviceInstance.Create(Definition.ToReference()).WithParameter(SemitonesParameter, Semitones.ToStored(semitones));
    }

    public void SetParameter(ParameterId parameter, ControlValue value)
    {
        if (parameter == SemitonesParameter)
        {
            _semitones = (int)Math.Round(Semitones.ToPlain(value));
        }
    }

    public void Process(in SignalBlock block, ReadOnlySpan<SignalEvent> input, SignalBuffer output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var e in input)
        {
            if (_semitones == 0)
            {
                output.Add(e);
                continue;
            }

            TrackEvent? moved = e.Event switch
            {
                NoteEvent n => n.Note.TryTranspose(_semitones, out var note) ? n with { Note = note } : null,
                NoteOffEvent n => n.Note.TryTranspose(_semitones, out var note) ? n with { Note = note } : null,
                PolyPressureEvent p => p.Note.TryTranspose(_semitones, out var note) ? p with { Note = note } : null,
                _ => e.Event,
            };

            if (moved is null)
            {
                _dropped++;
            }
            else
            {
                output.Add(e.With(moved));
            }
        }
    }

    public void Reset()
    {
    }

    public IReadOnlyList<string> TakeReport()
    {
        if (_dropped == 0)
        {
            return [];
        }

        var message = string.Create(CultureInfo.InvariantCulture, $"{_dropped} notes were transposed outside the MIDI range and will not play.");
        _dropped = 0;
        return [message];
    }
}
