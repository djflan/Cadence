# Architecture decision records

Consequential, hard-to-reverse decisions are recorded here as short ADRs. An ADR is never edited to
change its decision; a later ADR supersedes it and the superseded record is marked as such.

| ADR | Title | Status |
| --- | ----- | ------ |
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-build-and-test-conventions.md) | Build and test conventions | Accepted |
| [0003](0003-deterministic-event-ordering.md) | Deterministic ordering of simultaneous events | Accepted |
| [0004](0004-musical-time-representation.md) | Musical time representation | Accepted |
| [0005](0005-midi-endpoint-contracts.md) | MIDI endpoint contracts | Accepted |
| [0006](0006-playback-engine.md) | Playback engine: prepared plans, dual cursors, runtime releases | Accepted |
| [0007](0007-device-profile-format.md) | Data-first device profiles | Accepted |
| [0008](0008-routing-and-rebinding.md) | Routing: profiles and endpoints bound independently | Accepted |
| [0009](0009-project-persistence.md) | Project persistence: versioned JSON with atomic saves | Accepted |
| [0010](0010-desktop-ui-framework.md) | Desktop UI framework: Avalonia | Accepted |
| [0011](0011-coremidi-adapter.md) | macOS CoreMIDI adapter in managed code | Accepted |
| [0012](0012-performance-baseline.md) | Performance baseline: managed scheduling with OS real-time policy | Accepted |
| [0013](0013-windows-playback-timing.md) | Windows playback timing: 1 ms timer resolution and MMCSS | Accepted |
| [0014](0014-winmm-adapter.md) | Windows WinMM output adapter in managed code | Accepted |
| [0015](0015-winmm-system-exclusive.md) | System exclusive through WinMM without blocking playback | Accepted |
| [0016](0016-alsa-adapter.md) | Linux ALSA sequencer output adapter in managed code | Accepted |
| [0017](0017-recording-and-note-editing.md) | MIDI recording and note editing | Accepted |
| [0018](0018-midi-protocol-layering.md) | MIDI protocol layering: a protocol-independent domain with MIDI 1.0 first | Accepted |
| [0019](0019-sysex-preservation-and-interpretation.md) | SysEx: preserved losslessly, interpreted optionally by dialect | Accepted |
| [0020](0020-hybrid-tracks-clips-and-automation.md) | Hybrid tracks: typed clips and track automation lanes | Proposed |

## Template

```markdown
# NNNN. Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD

## Context
What forces are at play, including measurements where relevant.

## Decision
What we will do.

## Consequences
What becomes easier or harder, and what we must now watch for.
```
