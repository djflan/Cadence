# 0020. Hybrid tracks: typed clips and track automation lanes

- Status: Proposed
- Date: 2026-10-10

## Context

A track is a name, mute and solo, and one list of events at absolute ticks. The arrangement shows one
"region" per track, computed when it is drawn from the bar of the first event to the bar of the last.
Moving a region moves every event on the track. There is nothing to split, trim, or arrange.
"Automation" means controller, pitch bend, and pressure events on that same list, so there is no way
to draw a curve over the arrangement without it being part of the notes.

Cadence will eventually play audio and hosted instruments as well as MIDI. Many DAWs separate these
with track types (MIDI, instrument, audio), and converting between types later is awkward. Bitwig
Studio has a single hybrid track type instead: the clip's type decides what it holds (notes or audio),
any clip type can sit on any track, and automation lives on the track's timeline as well as inside
clips. Cadence has no track types yet, so it can go hybrid from the start without merging anything.

## Decision

### Tracks hold clips, and the clip type decides the content

- **There is no track kind.** A `Track` holds `Clips`, `Automation` lanes, a name, and mute and
  solo. Routing stays outside the track (ADR 0008).
- **`Clip` is an abstract record** with `Id` (`ClipId`), `Start`, `Length` (at least one tick),
  `ContentOffset`, and `Name`. `NoteClip` holds an `EventList` of the existing `TrackEvent`s. An
  `AudioClip` will be added once there is an audio engine, under its own ADR. The format reserves the
  `"audio"` clip type for it.
- **Content positions are relative to the clip.** The content origin is `Start - ContentOffset`, so
  moving a clip touches no events.
- **Trimming is non-destructive.** A clip plays content ticks `[ContentOffset, ContentOffset +
  Length)`. Content outside that window is kept but neither played nor drawn. Extending a clip
  left of its content origin shifts the content and resets the offset to 0, so positions stay
  non-negative.
- **The clip's bounds are the whole truth.** Notes that cross the clip end are cut at the end. Events
  that start before the window do not play, and nothing is retriggered. A clip's length never
  changes on its own when its events change.
- **Clips on a track never overlap.** The `Track` constructor refuses overlaps. Placing a clip
  (moving, copying, recording) lets the placed clip win: clips it covers are removed, trimmed, or
  split. Undo restores them. This keeps a track's arranged events a plain concatenation, with no
  merge rule.
- **Splitting divides events by timeline position.** Events keep their `EventId`s; the right half
  gets a new `ClipId`. Event IDs stay unique across all clips on a track.
- **Clip content is inline, not pooled.** Linked clips (several placements sharing one content) are
  future work: they would add a content pool to the sequence and make event identity `(ClipId,
  EventId)`. Keeping placement fields separate from content means clip placement, the arranger, and
  the compiler do not change when that happens.

### Track automation is separate from clip content

- **Controller data recorded or drawn in a clip stays in the clip** and moves with it.
- **Automation lanes belong to the track** and sit in arrangement time. They do not move with clips.
  A lane has an `AutomationTarget` and a list of `AutomationPoint`s.
- **Targets are MIDI parameters on a channel:** a controller number, pitch bend, or channel pressure.
  Bank select, data entry and increment/decrement, RPN/NRPN selectors, and channel mode messages are
  refused, because a lone value of these means something different from a curve. Mixer and plugin
  parameters become targets when they exist.
- **A point is a position, a `ControlValue`, and a curve** (`Hold` or `Linear`) shaping the segment
  up to the next point. Values are 32-bit (ADR 0018), so a lane already has MIDI 2.0 resolution.
- **Lanes are rendered into channel events when the plan is compiled.** The first value is sent at
  tick 0, so chasing a position is deterministic. Linear segments are sampled every PPQN/32 ticks, and
  a sample is sent only when its value differs at the target's MIDI 1.0 resolution (7 bits, or 14 for
  pitch bend). The plan compiler and the SMF exporter share this rendering, so exported files carry
  automation as controller events.
- **Automation wins over clip content.** Clip events aimed at the same target as a non-empty lane,
  after the route's channel override, are left out of the plan and reported once per track. If a
  channel override sends two lanes to the same target, the first lane wins. At equal times, automation
  sorts after clip events.
- **Lanes follow the track's route.** The channel override applies to them; transposition does not.

### Format 3

The project format becomes version 3 (ADR 0009): each track writes `clips` (with event ticks
relative to the clip) and `automation`. Format 2 projects are migrated by wrapping each track's
events in one note clip. The clip starts on the bar of the first event and ends on the bar line after
the last event's end, strictly after every event position, so nothing that played before is cut
off. A migrated project must send and export exactly the same MIDI 1.0 bytes; `PlanEquivalenceTests`
guards this with fixtures written by the format 2 serializer.

## Consequences

- Splitting, trimming, copying, and moving parts of a track become possible, and a later audio clip
  needs no new track type.
- Everything that read `Track.Events` changes: the plan compiler, SMF import and export, recording,
  the piano roll, and the event list. The piano roll edits one clip at a time. Recording a take
  creates a clip, and overdubbing joins the clips it overlaps.
- **Loop wraps do not chase** (ADR 0006): a held automation value at the loop start is not resent
  when the loop wraps, only at the next point. Clip controller events already behave this way, but
  automation makes it far more noticeable. A chase state computed with the plan and sent at the wrap
  would fix it without work on the playback thread.
- Re-importing an exported file brings automation back as clip controller events, not lanes,
  because a MIDI file cannot tell them apart.
- Sampling interval and deduplication set how dense automation output is. A MIDI 2.0 encoder will
  dedupe at 32 bits instead and send more messages for the same curve.
