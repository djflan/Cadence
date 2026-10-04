# 0003. Deterministic ordering of simultaneous events

- Status: Accepted
- Date: 2026-10-04

## Context

Many MIDI events share a tick: a bank select, program change, volume, and the first note of a part;
a note ending exactly where the next one starts; an RPN parameter selection followed by data entry.
Instruments act on messages in arrival order, so the dispatch order of simultaneous events changes
the sound. Standard MIDI Files do not guarantee a musically correct order, and merging tracks
introduces further ties.

## Decision

Events are ordered by tick, then by `EventPhase`, then by a stable sequence:

| Phase | Events | Reason |
| ----- | ------ | ------ |
| 0 `NoteOff` | Note releases, including note-on velocity 0 | End old notes before state changes, and before a retrigger on the same pitch |
| 1 `Meta` | Text, markers, unknown meta | No sound; listed for completeness |
| 2 `SystemExclusive` | SysEx and raw escaped bytes | Device setup (e.g. XG System On) must precede what depends on it |
| 3 `BankSelect` | CC 0 and CC 32 | A bank takes effect only at the next program change |
| 4 `ProgramChange` | Program change | Must follow bank select, precede notes |
| 5 `Control` | Other CCs, RPN/NRPN, pitch bend, pressure, channel mode | Shape the notes that follow |
| 6 `NoteOn` | Note starts | Sound last, after the state they depend on |

Ties within a phase keep their order within the track, so sequences like RPN MSB → LSB → data entry
are never reordered. When tracks are merged for playback, the remaining ties are broken by track
order and then by position within the track.

Paired notes (`NoteEvent`) have a duration of at least one tick, so a note's release can never be
ordered before its own start. Zero-length notes found on import are lengthened to one tick and
reported.

## Consequences

- Playback is reproducible: the same sequence always yields the same byte stream.
- Imported files whose producers relied on a different order (for example, a program change
  deliberately sent *before* its bank select) will be reordered. That is almost always a fix, but
  Standard MIDI File export may not reproduce the original byte order exactly.
- Overlapping notes of the same pitch on the same channel are dispatched as written, so the first
  release silences both on most instruments. This matches common sequencer behavior and is not
  corrected silently.
