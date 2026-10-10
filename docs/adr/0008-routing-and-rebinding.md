# 0008. Routing: profiles and endpoints bound independently, resolved at run time

- Status: Superseded by 0023 (its profile and endpoint resolution rules carry over, per instrument port)
- Date: 2026-10-04

## Context

A track must reach an instrument through two independent choices: *what* the instrument understands
(device profile) and *where* messages go (endpoint). Endpoints come and go, are renamed, and get
new OS identifiers; profiles may be uninstalled. Projects must survive all of this without losing
musical data or configuration.

## Decision

- `TrackRoute` (domain) stores a `ProfileReference` (stable profile ID plus last-known name) and an
  `EndpointReference` (provider, stable key, plus name/manufacturer/model hints) separately, with an
  optional channel override, transpose (±48), and voice assignment (profile bank ID + program).
  Routes are persisted as written, resolved or not.
- `RouteResolver` (application) checks routes against what is available now:
  - profiles by ID only — never inferred from an endpoint's name;
  - endpoints by exact stable ID; failing that, by remembered display name **within the same
    provider**, giving `BoundByName` only if exactly one matches (playable, but the UI asks the user
    to confirm), `Ambiguous` for several, `Missing` for none;
  - every unresolved state carries a plain-language explanation.
- A missing profile never blocks playback; it only removes names and setup messages. A missing
  endpoint silences only that track.
- `PlaybackRouting` turns resolved routes into plan bindings: one output slot per distinct endpoint,
  voice selection rendered from the profile (bank MSB, LSB, program) at tick 0 ahead of the track's
  own events, and transposition applied to notes and keyed messages. Opening outputs reports
  failures instead of throwing.
- `EndpointDirectory` presents all providers as one, dispatching by provider ID.

## Consequences

Name-based rebinding is a convenience with a visible status, not a silent rewrite: the stored
reference changes only when the user confirms. Architecture tests enforce that `Cadence.Midi` and
`Cadence.Profiles` never reference each other.
