# Cadence architecture

Cadence models music and musical intent. MIDI 1.0, MIDI 2.0/UMP, SysEx dialects, and operating-system
MIDI APIs sit at the edges. This page shows where things live and why. The decisions behind it
are in [docs/adr/](adr/), in particular [ADR 0018](adr/0018-midi-protocol-layering.md) and
[ADR 0019](adr/0019-sysex-preservation-and-interpretation.md).

## Layers

```text
UI / Application         Cadence.Desktop, Cadence.Presentation, Cadence.Application
       ↓
Sequencer / Domain       Cadence.Domain, Cadence.Profiles, Cadence.Playback
       ↓
MIDI semantics           channel events (NoteEvent, ControllerEvent, ProgramEvent, …) and ControlValue
       ↓
Protocol encoding        Cadence.Midi: Wire (MIDI 1.0 bytes), Files (SMF), SysEx (dialects)
       ↓
Platform transport       Cadence.Midi.Endpoints contracts; Cadence.Platform.CoreMidi / Windows / Alsa
```

These are conceptual boundaries, not one assembly each. A layer gets its own project only when that
buys something concrete: a dependency rule that must be enforced, an operating-system-specific
build, or a framework that must stay out of testable code. Project persistence lives in
`Cadence.Infrastructure` for the same reason: it may depend on the domain and nothing else.

### Projects and their references

```text
Cadence.Domain            (nothing)          sequence, tracks, clips, automation, events, time, routes, MIDI values
Cadence.Midi              Domain             MIDI 1.0 encoding, SMF, SysEx dialects, endpoint contracts
Cadence.Profiles          Domain             device profiles: what an instrument understands
Cadence.Infrastructure    Domain             project files
Cadence.Playback          Domain, Midi       real-time engine, prepared plans
Cadence.Application       all of the above   sessions, editing, recording, routing
Cadence.Presentation      Application        view models, no UI framework
Cadence.Desktop           Presentation, Platform.*   Avalonia views, platform selection
Cadence.Platform.*        Midi               CoreMIDI, WinMM, ALSA adapters
```

`DependencyDirectionTests` enforces this. `Cadence.Midi` and `Cadence.Profiles` never reference
each other, and the domain references only the base class library.

### Where does this belong?

| You are adding… | Put it in |
| --------------- | --------- |
| A musical concept or edit (a new event kind, a quantize rule) | `Cadence.Domain`, with commands in `Cadence.Application/Editing` |
| How a channel event becomes MIDI 1.0 messages (bank and program, value resolution) | `Cadence.Midi/Wire/Midi1Encoder.cs` |
| How MIDI 1.0 messages from files or input become channel events | `Cadence.Midi/Wire/Midi1Decoder.cs` |
| Reading or writing `.mid` files | `Cadence.Midi/Files` |
| Recognizing a SysEx family (Universal, XG, GS, another manufacturer) | `Cadence.Midi/SysEx` |
| Facts about one instrument (banks, voices, drum names, setup SysEx) | a JSON profile in `/profiles`; the loader is `Cadence.Profiles` |
| An operating-system MIDI API | `Cadence.Platform.<OS>`, implementing `IMidiEndpointProvider` |
| The project file format or a migration | `Cadence.Infrastructure/Projects` |
| How something is shown or edited | `Cadence.Presentation` (logic), `Cadence.Desktop` (views) |

### Abstractions

Interfaces exist only where something is actually substituted:

- `IMidiEndpointProvider`, `IMidiOutput`, `IMidiInput`: each platform adapter, plus the loopback test double.
- `IMonotonicClock`: the real clock, and a virtual clock for deterministic tests.
- `IProjectCommand`, `IProjectMigration`: undoable edits and format upgrades.
- `IUserInteraction`, `IUiDispatcher`: keep the UI framework out of the view models.

Everything else is concrete. SysEx dialects, for example, are static classes chosen by a `switch`
on the manufacturer ID, because nothing needs to swap them at run time.

## MIDI strategy

```text
                         CADENCE
                            │
                    Sequencer domain
                            │
                     MIDI semantics
                            │
               ┌────────────┴────────────┐
               │                         │
            MIDI 1.0                  MIDI 2.0  (planned)
               │                         │
         byte encoding                  UMP
               │                         │
               └────────────┬────────────┘
                            │
                     MIDI transport
                            │
                         Devices


       SysEx interpretation is orthogonal:

                       Raw SysEx
                           │
               ┌───────────┼───────────┐
               │           │           │
           Universal      XG          GS
                        Yamaha       Roland
               │           │           │
               └───────────┴───────────┘
                           │
                  Semantic metadata
```

### MIDI 1.0: first-class

MIDI 1.0 is fully supported and is not a compatibility layer. Hardware, Standard MIDI Files, and
XG-era instruments all depend on it. Its specific concerns stay in `Cadence.Midi`:

- The domain stores channel events, not messages: `NoteEvent`, `NoteOffEvent`, `ControllerEvent`,
  `ProgramEvent`, `PitchBendEvent`, `ChannelPressureEvent`, and `PolyPressureEvent`. Controller,
  pitch bend, and pressure values are `ControlValue`s at MIDI 2.0 resolution (32 bits). MIDI 1.0
  values are scaled up with the MIDI 2.0 min-center-max rule, so they scale back down exactly.
- `Wire/Midi1Encoder` turns events into messages. For example, a `ProgramEvent` (bank MSB, bank
  LSB, program) becomes CC 0, CC 32, then a program change, a `NoteEvent` (a note with a duration)
  becomes a note-on and a note-off, and values are reduced to 7 bits, or 14 for pitch bend.
- `Wire/Midi1Decoder` is the inverse, used by SMF import and recording. Bank selects on the same
  channel and tick as a program change are folded into it. Anywhere else they stay controller
  events, so what is sent is unchanged. `MidiOneOutputGoldenTests` checks that the shipped samples
  play and export exactly the same MIDI 1.0 bytes.
- `Wire/ChannelMessage` is the compact MIDI 1.0 message (status byte plus data bytes). It is used
  in the playback plan and the real-time engine, which only send MIDI 1.0 today.
- `Wire/MidiWire` validates complete messages, and `Wire/MidiStreamParser` handles running status,
  realtime bytes inside other messages, and SysEx split across driver packets.
- `Files/` reads and writes SMF with running status, and pairs notes, including a note-on with
  velocity 0 counting as a release.

### MIDI 2.0 and UMP: planned

Cadence does not send MIDI 2.0 yet. The design leaves room for it:

- MIDI 2.0 extends MIDI 1.0. UMP (Universal MIDI Packet) is a packet format that carries both MIDI
  1.0 and MIDI 2.0 protocol messages. It is a wire representation, not Cadence's model.
- A MIDI 2.0 encoder will sit beside `Midi1Encoder` (in `Cadence.Midi/Ump/`), producing UMP from the
  same semantic operations. In MIDI 2.0 some operations become atomic: a `ProgramSelection` is one
  program change with a bank, not three messages.
- Transport gets a separate UMP send path with its own capability flag (ADR 0005). Platform
  adapters for Windows MIDI Services and CoreMIDI's event lists are natural UMP endpoints.
- The playback plan compiler encodes every event today with `Midi1Encoder`. It will pick the
  encoder for each output from its capabilities.
- The domain already holds controller, pitch bend, and pressure values at MIDI 2.0 resolution, and
  a program selection as one event. MIDI 2.0 also offers 16-bit velocity, per-note controllers, and
  per-note pitch bend, which Cadence does not model yet. Some of this has no lossless MIDI 1.0 form.
  The MIDI 1.0 encoder already reduces values to 7 or 14 bits, and should report what was lost, in
  the same way SMF export reports today, once such data can be created.

### MIDI-CI: future

MIDI Capability Inquiry (Discovery, Profiles, Property Exchange) could let Cadence ask a modern
device for its capabilities, program lists, and controller names, instead of relying only on
shipped profiles. It needs a paired input and output on one device, so it will sit above the
endpoint contracts, and what it learns will feed device knowledge, not transport. Legacy
instruments do not answer MIDI-CI, so data profiles and the XG and GS interpreters stay essential.

### SysEx: always preserved, optionally understood

SysEx is handled at two levels:

1. **Lossless opaque transport.** A complete message is a `SysExMessage` (`F0`…`F7`, validated
   7-bit data, at most 1 MiB) in a `SysExEvent`. Bytes that are not one complete message (split
   packets, `F7` escapes, malformed SysEx) are kept as a `RawMidiEvent`. SMF import and export,
   project files, and playback carry both byte for byte. Unknown SysEx is not invalid SysEx.
2. **Semantic interpretation.** `SysExInterpreter.Interpret` recognizes Universal SysEx (`7E`,
   `7F`), Yamaha XG (`43`, model `4C`), and Roland GS (`41`, model `42`). It returns `null` for
   anything else. Interpretations are worked out when needed and never stored, so they can never
   replace or corrupt the original bytes. A message with a bad checksum is reported as such, not
   repaired. Today the MIDI monitor and the event list use them to name messages.

**Universal SysEx** (GM System On/Off, GM2 On, Identity Request/Reply, master volume, balance,
and tuning) is kept separate from manufacturer SysEx and does not depend on Yamaha or Roland.

**XG** (`XgDialect`) recognizes parameter changes, including XG System On and All Parameter Reset.
It sorts them into system, reverb, chorus, variation, insertion, multi part (with the part number),
and drum setup (with the note), and it recognizes bulk dumps with their checksum. Parameter names
within each block, model-specific extensions such as the QY100's, and requests are future work.

**GS** (`GsDialect`) recognizes data set (DT1) messages: GS Reset, system mode, system, reverb,
chorus, common, part (using GS's own block-to-part numbering), and drum map. It checks the Roland
checksum. `GsDialect.Checksum` will also be used when Cadence renders GS messages.

XG and GS each have their own model. They share only the `SysExInterpretation` base and the
dispatcher. To add a dialect, add a file to `Cadence.Midi/SysEx`, add a case to the dispatch, and
add tests in `Cadence.Tests.Unit/Midi/SysEx`.

Dialect interpreters understand a protocol family in code. Device profiles describe one instrument
as data. A profile's `protocols: ["xg"]` declares compatibility, but the interpreter reads XG
messages whichever profile a track is routed to.

### Device knowledge is not transport

| Concern | Examples | Lives in |
| ------- | -------- | -------- |
| Transport | CoreMIDI, WinMM, ALSA, future Windows MIDI Services | `Cadence.Platform.*` |
| Protocol | MIDI 1.0 bytes, SMF, future UMP | `Cadence.Midi` |
| Dialect and device semantics | Universal, XG, GS, device profiles | `Cadence.Midi/SysEx`, `Cadence.Profiles`, `/profiles` |

An adapter never interprets what it carries, and an interpreter never cares where the bytes came
from (ADR 0005, 0007, 0008).

## Tracks, clips, and automation

A track has no type (ADR 0020). It holds clips, whose type decides what they contain, and automation
lanes. Today the only clip type is `NoteClip`, holding events at positions relative to the clip's
content origin (its start, less what is trimmed off the left); an audio clip will join it once there
is an audio engine. A clip shows a window of its content, so trimming loses nothing, and clips on a
track never overlap. `Track.ArrangedEvents` is what the clips play, at timeline positions.
`TrackRendering` adds the automation lanes, sampled into channel events, and leaves out clip events
a lane replaces; the plan compiler and SMF export both use it.

## Known compromises

These are deliberate for now and are where MIDI 2.0 work will start:

- **Notes are MIDI 1.0-shaped.** Note velocity is 7-bit (MIDI 2.0 has 16 bits), and notes have no
  per-note attributes, controllers, or pitch bend. See [Planned work](#planned-work).
- **RPN and NRPN stay controller sequences** (CC 101/100 or 99/98, then 6 and 38). Folding them into
  single parameter events cannot always reproduce the original bytes. For example, a message that
  sends only CC 6 leaves the device's previous LSB in place. A MIDI 2.0 encoder can translate the
  sequences as it sends them, as the MIDI 2.0 translation rules describe.
- **Sixteen channels.** `MidiChannel` is 1 to 16. UMP adds 16 groups. The group belongs to routing
  (`TrackRoute`) and the endpoint, not to the music.
- **The playback plan is MIDI 1.0.** `PlaybackPlan` and `ChaseState` hold encoded `ChannelMessage`s,
  so chasing controller state on seek is worked out in MIDI 1.0 terms. A MIDI 2.0 output will need
  its plan entries in UMP form.
- **`EventPhase.BankSelect`** (ADR 0003) now applies only to bank selects that are not part of a
  `ProgramEvent`. A `ProgramEvent` sends its bank select messages in the program change phase,
  directly before its program change.

## Planned work

### 16-bit velocity

Note and release velocity move to MIDI 2.0's 16 bits, the same way controller values did:

- `Velocity` holds 16 bits, scaled from MIDI 1.0 with the min-center-max rule, so every 7-bit
  velocity converts up and back down exactly. Imported songs keep playing byte for byte, and
  `MidiOneOutputGoldenTests` checks it.
- `Velocity.Value` is removed rather than given a new meaning. In its place come `ToSevenBit()`
  and a 16-bit accessor, so every use has to be revisited. The MIDI 1.0 side (`ChannelMessage`,
  the playback plan, the metronome) takes a plain 7-bit value, and `Midi1Encoder` converts.
- A note-on is always sent to MIDI 1.0 with velocity 1 or more, because 0 there means note-off. A
  small 16-bit velocity that scales down to 0 is sent as 1.
- Cadence notes keep requiring a velocity above zero. MIDI 2.0 allows a silent note-on at
  velocity 0, but nothing can create one yet.
- Editing shows and steps velocity in familiar 1–127 units (velocity lane, Alt + ↑ / ↓, event
  list, inspector, computer keyboard) and stores the exact scaled value. Nudging a
  higher-resolution velocity snaps it to the 7-bit grid.
- Velocity edits (scale, compress, humanize, ramps) keep calculating in 7 bits at first, so
  results match today exactly. Calculating in 16 bits is a separate, deliberate change, because
  results would then round differently.
- Release velocity gets the same treatment, for `NoteEvent` and `NoteOffEvent`.
- The project file stores 16-bit velocities. No migration is needed while there are no saved
  projects to preserve.

Not included: MIDI 2.0 note attributes (articulation, per-note pitch) and per-note controllers.
They follow once a MIDI 2.0 endpoint can play them.

### Controller ranges and steps

MIDI 2.0 messages always carry a controller's full range. Which part is meaningful is device
knowledge. MIDI-CI Property Exchange describes it per controller (the Controller Resources
specification) in one of two ways. `stepCount` quantizes the full range into N steps, and is the
preferred method. `minMax` marks a sub-range, and is intended for legacy products. An entry uses
only one of them.

Cadence keeps events at full resolution and treats ranges and steps as profile data.
`ProfileController` (number, name, default) can gain optional `steps` or `min`/`max`. These would
be written by hand for legacy instruments or filled in from MIDI-CI later. Controller lanes would
then snap drawing to the steps and show values in the device's own units. Nothing supplies this
data yet, so it waits.

## Decisions at a glance

| Decision | Recorded in |
| -------- | ----------- |
| The domain is protocol-independent; MIDI sits at the edges | ADR 0018 |
| MIDI 1.0 remains first-class | ADR 0018 |
| MIDI 2.0/UMP is a planned protocol target | ADR 0018, ADR 0005 |
| UMP is not the domain model | ADR 0018 |
| Raw SysEx is preserved losslessly | ADR 0019 |
| SysEx interpretation is optional, derived, and extensible | ADR 0019 |
| XG and GS are dialect interpreters, not core MIDI concepts | ADR 0019 |
| Device semantics are independent of platform transport | ADR 0005, ADR 0007, ADR 0008 |
| Simplicity over speculative abstraction | ADR 0018 |
