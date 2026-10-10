# 0018. MIDI protocol layering: a protocol-independent domain with MIDI 1.0 first

- Status: Accepted
- Date: 2026-10-06

## Context

Bluestone must work very well with MIDI 1.0 hardware, Standard MIDI Files, and XG-era instruments,
and must later reach MIDI 2.0 devices over UMP. The two protocols differ in more than byte layout:
MIDI 2.0 has higher resolution and per-note expression, and makes some multi-message MIDI 1.0
operations atomic (bank and program selection is one message). Some MIDI 2.0 data has no lossless
MIDI 1.0 form.

Encoding had leaked upward. `DeviceProfile.SelectionMessages` in `Bluestone.Profiles` built the CC 0,
CC 32, and program change messages for a voice, and `NoteEvent` produced its own note-on and note-off
messages. Neither profiles nor domain notes should know how they travel.

## Decision

- The domain models musical intent. Semantic values such as `ProgramSelection` (program plus
  optional bank MSB and LSB) and `NoteEvent` (a note with a duration) do not encode themselves.
  `ProfileBank.Select` returns a `ProgramSelection`. It no longer returns messages.
- MIDI 1.0 is a first-class protocol, not a compatibility layer. Its encoding lives in
  `Bluestone.Midi/Wire`: `Midi1Encoder` for semantic operations, `MidiWire` and `MidiStreamParser` for
  bytes. Standard MIDI File support lives in `Bluestone.Midi/Files`.
- MIDI 2.0 is a planned protocol target. Its encoder will sit beside `Midi1Encoder` and produce UMP
  from the same semantic operations. Transport gets a separate UMP path with its own capability
  (ADR 0005). UMP is a wire representation and never becomes the domain model, and neither do
  MIDI 1.0 byte messages.
- Conceptual layers (UI, domain, MIDI semantics, protocol encoding, transport) do not each get a
  project. A project boundary must buy something concrete: an enforced dependency rule, an
  operating-system-specific build, or keeping a framework out of testable code. Interfaces exist only
  where something is actually substituted. The current map is in
  [docs/architecture.md](../architecture.md).
- Channel events are semantic records (`ControllerEvent`, `ProgramEvent`, `PitchBendEvent`,
  `ChannelPressureEvent`, `PolyPressureEvent`, `NoteOffEvent`, and `NoteEvent`), not MIDI 1.0
  messages. Their values are `ControlValue`s at MIDI 2.0 resolution (32 bits), scaled from MIDI 1.0
  with the MIDI 2.0 min-center-max rule so they scale back down exactly. `ChannelMessage` moves to
  `Bluestone.Midi/Wire`. `Midi1Decoder` turns messages into events. It folds bank selects into a
  program change only on the same channel and tick, so MIDI 1.0 output does not change. Project
  files store the events (format version 2). Format 1 files are not migrated, because Bluestone has
  no users with saved projects yet.
- RPN and NRPN stay controller sequences, because folding them cannot always reproduce the
  original messages.

## Consequences

- Adding MIDI 2.0 means adding an encoder and a send path. Profiles and the domain do not change.
- Code that needs the MIDI 1.0 form of a semantic value asks `Midi1Encoder`, so MIDI 1.0
  conventions such as bank select ordering have one home.
- MIDI 1.0 playback and export must stay byte-identical for the same input.
  `MidiOneOutputGoldenTests` checks this for the shipped samples, per output and channel.
- Note velocity is still 7-bit, and the playback plan and chase state are still MIDI 1.0. Those are
  where MIDI 2.0 output work starts.
