# 0027. Project format 4 and the migration from per-track routes

- Status: Accepted; amends 0009
- Date: 2026-10-10

## Context

Format 3 stored one `routing` entry per track (ADR 0008). The new model (ADRs 0021 to 0024) adds track
roles and groups, audio clips, device chains with parameters, bypass, and plugin state, external
instruments, connections, mixer channels, and device automation targets. Existing projects must open
and play exactly as before.

## Decision

- `formatVersion` 4 (`ProjectSerializer.CurrentFormatVersion`) adds to the project: `instruments`,
  `chains`, `connections`, and `mixer`; and to tracks: `role`, optional `group`, `audio` clips with a
  `source`, and `device` automation targets. `routing` is gone. The format is documented in
  `docs/project-format.md`.
- Everything is stored whether or not it can be resolved now: missing profiles, endpoints, plugins,
  devices, and connections that do not validate are kept and reported, never dropped on load.
- Plugin state is stored as base64 with its format string; Cadence never interprets it.
- `Format3To4Migration` converts each format 3 route with `TrackOutputs.Write`, the same code the
  inspector uses: one external instrument per distinct endpoint and profile (named after the endpoint,
  or "Unassigned" for a route with settings and no endpoint), a connection forcing the route's channel
  and selecting its voice, and a Transpose device for a non-zero transposition. Tracks get the
  `instrument` role; the mixer starts empty.
- On load, `TrackRoleConversion.Reconcile` widens any role that cannot show the track's clips and
  reports the change (`ProjectDocument.Notes`).
- Chain presets have their own file format, `cadence-chain-preset` version 1, without identities or
  connections (ADR 0022).

## Consequences

- The frozen format 3 fixtures send byte-identical MIDI 1.0 through the new pipeline and reopen
  unchanged after saving as format 4 (`Format3EquivalenceTests`, `Format3To4MigrationTests`); the format 2
  fixtures still pass through 2 → 3 → 4 (`PlanEquivalenceTests`).
- Files written by this version are refused by older versions, as ADR 0009 requires.
- Unknown properties inside the project object are still not preserved; only top-level ones are (as before).
