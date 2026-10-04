# 0004. Musical time representation

- Status: Accepted
- Date: 2026-10-04

## Context

Sequencing needs exact, drift-free conversion between musical positions (ticks, bars, beats) and
elapsed time across arbitrary tempo changes, while staying interchangeable with Standard MIDI Files.

## Decision

- Positions and durations are distinct types: `Tick` (non-negative position) and `TickSpan`
  (non-negative duration), both backed by `long`.
- `Ppqn` is limited to 1–32767 so every sequence fits a Standard MIDI File header. New sequences use 960.
- `Tempo` is stored as microseconds per quarter note (1–16,777,215, the 24-bit file limit);
  beats per minute are derived, never stored.
- `TempoMap` accumulates elapsed time as an exact `Int128` numerator (µs × ticks) and divides by
  PPQN once, flooring to `TimeSpan` resolution (100 ns). Error never accumulates across tempo
  changes. `TickAt` is the exact inverse: the last tick whose time is at or before the target.
- `MeterMap` restarts bar numbering at each time signature change; a change landing mid-bar
  ends that bar early. A meter whose beat is not a whole number of ticks (for example 3/8 at 1 PPQN)
  is rejected rather than approximated.
- Wall-clock and monotonic time are separate concerns handled by playback clocks, not by the domain.

## Consequences

`TimeSpan`'s 100 ns resolution is far finer than MIDI transport jitter. At extreme
resolutions (ticks shorter than 100 ns) several ticks map to the same `TimeSpan`, and the
round-trip property weakens to `TickAt(TimeAt(t)) ≥ t`; this is tested. Importing such a file
with a meter incompatible with its resolution must be handled and reported by the importer.
