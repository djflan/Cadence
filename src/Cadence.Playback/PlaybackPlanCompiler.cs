using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Wire;

namespace Cadence.Playback;

/// <summary>Turns an edit-model <see cref="Sequence"/> into an immutable <see cref="PlaybackPlan"/>.</summary>
public static class PlaybackPlanCompiler
{
    /// <summary>
    /// Compiles the tracks that have a binding. A track plays when no track is soloed and it is not
    /// muted, or when it is soloed (solo overrides mute). Meta events are not transmitted.
    /// </summary>
    public static PlaybackPlan Compile(Sequence sequence, IReadOnlyDictionary<TrackId, PlanTrackBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(bindings);

        var anySolo = sequence.Tracks.Any(t => t.IsSoloed);
        var entries = new List<(long Tick, EventPhase Phase, int Track, int Index, PlanEvent Event)>();
        var payloads = new List<ByteBlock>();
        var diagnostics = new List<PlanDiagnostic>();

        for (var trackIndex = 0; trackIndex < sequence.Tracks.Length; trackIndex++)
        {
            var track = sequence.Tracks[trackIndex];
            var audible = anySolo ? track.IsSoloed : !track.IsMuted;
            if (!audible)
            {
                continue;
            }

            if (!bindings.TryGetValue(track.Id, out var binding))
            {
                diagnostics.Add(new PlanDiagnostic(track.Id, "The track is not routed to an output and will not play."));
                continue;
            }

            ArgumentOutOfRangeException.ThrowIfNegative(binding.OutputSlot, nameof(bindings));
            var pendingSysEx = new List<byte>();
            long pendingSysExTick = 0;
            var transposedAway = 0;

            for (var i = 0; i < binding.InitialMessages.Length; i++)
            {
                var message = Rechannel(binding.InitialMessages[i]);
                Add(0, EventOrder.PhaseOf(message), i - binding.InitialMessages.Length, new PlanEvent(0, binding.OutputSlot, message, -1, 0));
            }

            for (var i = 0; i < track.Events.Length; i++)
            {
                var e = track.Events[i];
                var tick = e.Position.Value;
                switch (e)
                {
                    case NoteEvent note when binding.Transpose != 0 && !note.Note.TryTranspose(binding.Transpose, out _):
                        transposedAway++;
                        break;
                    case NoteEvent note:
                        var on = binding.Transpose == 0 ? note.OnMessage : (note with { Note = Transposed(note.Note) }).OnMessage;
                        Add(tick, EventPhase.NoteOn, i, new PlanEvent(tick, binding.OutputSlot, Rechannel(on), -1, note.Duration.Value, note.ReleaseVelocity));
                        break;
                    case ChannelEvent channel when !TryTransposeKeyed(channel.Message, out _):
                        transposedAway++;
                        break;
                    case ChannelEvent channel:
                        TryTransposeKeyed(channel.Message, out var keyed);
                        Add(tick, e.Phase, i, new PlanEvent(tick, binding.OutputSlot, Rechannel(keyed), -1, 0));
                        break;
                    case SysExEvent sysEx:
                        AddPayload(tick, i, sysEx.Message.Bytes);
                        break;
                    case RawMidiEvent raw:
                        AddRaw(raw, i);
                        break;
                }
            }

            if (transposedAway > 0)
            {
                diagnostics.Add(new PlanDiagnostic(track.Id, $"{transposedAway} notes were transposed outside the MIDI range and will not play."));
            }

            if (pendingSysEx.Count > 0)
            {
                diagnostics.Add(new PlanDiagnostic(track.Id, "A SysEx message split across events never ended and was not sent."));
            }

            void Add(long tick, EventPhase phase, int index, PlanEvent planEvent) => entries.Add((tick, phase, trackIndex, index, planEvent));

            void AddPayload(long tick, int index, ByteBlock bytes)
            {
                payloads.Add(bytes);
                Add(tick, EventPhase.SystemExclusive, index, new PlanEvent(tick, binding.OutputSlot, default, payloads.Count - 1, 0));
            }

            ChannelMessage Rechannel(ChannelMessage message) => binding.Channel is { } channel ? message.WithChannel(channel) : message;

            NoteNumber Transposed(NoteNumber note) => note.TryTranspose(binding.Transpose, out var result) ? result : note;

            // Note-on/off and polyphonic pressure carry a note number and move with the transposition.
            bool TryTransposeKeyed(ChannelMessage message, out ChannelMessage result)
            {
                result = message;
                if (binding.Transpose == 0 || message.Kind is not (ChannelMessageKind.NoteOn or ChannelMessageKind.NoteOff or ChannelMessageKind.PolyPressure))
                {
                    return true;
                }

                if (!message.Note.TryTranspose(binding.Transpose, out var note))
                {
                    return false;
                }

                ChannelMessage.TryCreate(message.Status, note.Value, message.Data2, out result);
                return true;
            }

            // Raw events are sent when they are a complete message on their own. SysEx split across
            // several events is joined and sent at the time of its first packet.
            void AddRaw(RawMidiEvent raw, int index)
            {
                var bytes = raw.Bytes.Span;
                if (pendingSysEx.Count == 0 && MidiWire.Classify(bytes) != MidiMessageClass.Invalid)
                {
                    AddPayload(raw.Position.Value, index, raw.Bytes);
                    return;
                }

                if (bytes.Length > 0 && bytes[0] == SysExMessage.Start)
                {
                    if (pendingSysEx.Count > 0)
                    {
                        diagnostics.Add(new PlanDiagnostic(track.Id, "A SysEx message split across events was interrupted by another and was not sent."));
                    }

                    pendingSysEx.Clear();
                    pendingSysExTick = raw.Position.Value;
                }
                else if (pendingSysEx.Count == 0)
                {
                    diagnostics.Add(new PlanDiagnostic(track.Id, "Raw bytes that are not a complete MIDI message were not sent."));
                    return;
                }

                pendingSysEx.AddRange(bytes);
                if (pendingSysEx.Count > SysExMessage.MaxLength)
                {
                    diagnostics.Add(new PlanDiagnostic(track.Id, "A split SysEx message exceeded the size limit and was not sent."));
                    pendingSysEx.Clear();
                    return;
                }

                if (pendingSysEx[^1] == SysExMessage.End)
                {
                    var joined = pendingSysEx.ToArray();
                    pendingSysEx.Clear();
                    if (MidiWire.Classify(joined) == MidiMessageClass.SystemExclusive)
                    {
                        AddPayload(pendingSysExTick, index, ByteBlock.Copy(joined));
                    }
                    else
                    {
                        diagnostics.Add(new PlanDiagnostic(track.Id, "A split SysEx message was malformed and was not sent."));
                    }
                }
            }
        }

        var ordered = entries
            .OrderBy(e => e.Tick)
            .ThenBy(e => e.Phase)
            .ThenBy(e => e.Track)
            .ThenBy(e => e.Index)
            .Select(e => e.Event)
            .ToArray();

        return new PlaybackPlan(sequence.TempoMap, ordered, [.. payloads], diagnostics);
    }
}
