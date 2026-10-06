# 0019. SysEx: preserved losslessly, interpreted optionally by dialect

- Status: Accepted
- Date: 2026-10-06

## Context

Real MIDI files and instruments carry SysEx that Cadence will never fully understand: other
manufacturers, other models, bulk dumps, and damaged messages. Cadence also wants to understand
the SysEx that matters most to its users, which is Universal SysEx, Yamaha XG, and Roland GS. XG and
GS look similar but have different address maps, part numbering, and checksums.

Before this decision Cadence preserved SysEx (`SysExEvent`, `RawMidiEvent`) but did not
interpret it. The monitor showed only a manufacturer byte.

## Decision

- **Preservation never depends on understanding.** Complete messages are `SysExMessage`s. Split,
  escaped, or malformed bytes are `RawMidiEvent`s. Both survive SMF import and export, project files,
  and playback byte for byte. Unknown SysEx is not invalid SysEx.
- **Interpretation is derived and optional.** `SysExInterpreter.Interpret` (in
  `Cadence.Midi/SysEx`) dispatches on the manufacturer ID to a dialect and returns a
  `SysExInterpretation`, or `null` when it does not recognize the message. Interpretations are not
  stored, so they cannot replace or corrupt the original bytes. A bad checksum is reported, not
  repaired.
- **Universal SysEx is its own dialect** (`UniversalDialect`), independent of any manufacturer.
- **XG and GS are dialect interpreters, not core MIDI concepts.** `XgDialect` and `GsDialect`
  each keep their own model (`XgParameterChange`, `XgBulkDump`, `GsDataSet`). Only the
  `SysExInterpretation` base and the dispatcher are shared. The generic types (`SysExMessage`,
  `MidiWire`) know nothing about Yamaha addresses or Roland checksums.
- Dialects are static classes chosen by a `switch`, not plug-ins behind an interface, because
  dispatch is fixed by manufacturer ID and nothing needs to swap them at run time.

## Consequences

- Adding a dialect is one file and one `switch` case, plus tests. Deeper XG and GS knowledge
  (parameter names, QY100-specific blocks, requests) can grow inside each dialect without touching
  the others.
- Descriptions are English strings on the interpretation records. If Cadence is localized, they
  move to the presentation layer and the records keep only structured data.
- Rendering checksummed messages from profiles (ADR 0007) can reuse `GsDialect.Checksum`, but
  still needs a profile schema change.
