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

            for (var i = 0; i < binding.InitialEvents.Length; i++)
            {
                AddChannel(0, Prepare(binding.InitialEvents[i], transpose: false)!, i - binding.InitialEvents.Length);
            }

            var rendered = TrackRendering.Render(track, sequence.Ppqn, binding.Channel);
            if (rendered.SuppressedEvents > 0)
            {
                diagnostics.Add(new PlanDiagnostic(track.Id, $"{rendered.SuppressedEvents} events in clips were replaced by the track's automation."));
            }

            if (rendered.DroppedLanes > 0)
            {
                diagnostics.Add(new PlanDiagnostic(track.Id, $"{rendered.DroppedLanes} automation lanes control the same thing as another lane once sent on the route's channel, and were left out."));
            }

            var events = rendered.Events;
            for (var i = 0; i < events.Length; i++)
            {
                var e = events[i];
                var tick = e.Position.Value;
                switch (e)
                {
                    case ChannelEvent channel when Prepare(channel, transpose: true) is { } prepared:
                        AddChannel(tick, prepared, i);
                        break;
                    case ChannelEvent:
                        transposedAway++;
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

            // The event as this binding sends it: on the binding's channel and, for events that carry a
            // note number, transposed. Null when transposition takes the note outside 0-127.
            ChannelEvent? Prepare(ChannelEvent e, bool transpose)
            {
                if (binding.Channel is { } channel)
                {
                    e = e with { Channel = channel };
                }

                if (!transpose || binding.Transpose == 0)
                {
                    return e;
                }

                return e switch
                {
                    NoteEvent n => n.Note.TryTranspose(binding.Transpose, out var note) ? n with { Note = note } : null,
                    NoteOffEvent n => n.Note.TryTranspose(binding.Transpose, out var note) ? n with { Note = note } : null,
                    PolyPressureEvent p => p.Note.TryTranspose(binding.Transpose, out var note) ? p with { Note = note } : null,
                    _ => e,
                };
            }

            // Channel events are sent as MIDI 1.0; a note's release is scheduled by the engine.
            void AddChannel(long tick, ChannelEvent e, int index)
            {
                if (e is NoteEvent note)
                {
                    Add(tick, EventPhase.NoteOn, index, new PlanEvent(tick, binding.OutputSlot, Midi1Encoder.NoteOn(note), -1, note.Duration.Value, note.ReleaseVelocity));
                    return;
                }

                foreach (var message in Midi1Encoder.Encode(e))
                {
                    Add(tick, e.Phase, index, new PlanEvent(tick, binding.OutputSlot, message, -1, 0));
                }
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

        return new PlaybackPlan(sequence.TempoMap, sequence.MeterMap, ordered, [.. payloads], diagnostics);
    }
}
