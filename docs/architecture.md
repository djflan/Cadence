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
MIDI semantics           Cadence.Domain.Midi values (ProgramSelection, NoteEvent, …)
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
Cadence.Domain            (nothing)          sequence, tracks, events, time, routes, MIDI values
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
| How a semantic operation becomes MIDI 1.0 messages (bank and program, RPN) | `Cadence.Midi/Wire/Midi1Encoder.cs` |
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

- `Wire/Midi1Encoder` turns semantic operations into messages. For example, a `ProgramSelection`
  (bank MSB, bank LSB, program) becomes CC 0, CC 32, then a program change, and a `NoteEvent` (a note
  with a duration) becomes a note-on and a note-off.
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
- The playback plan compiler picks the encoder for each output from its capabilities.
- MIDI 2.0 offers higher resolution, per-note controllers, and per-note pitch bend. Some of this has
  no lossless MIDI 1.0 form. When such data is sent to a MIDI 1.0 endpoint or written to an SMF, the
  MIDI 1.0 encoder will downscale it and report what was lost, in the same way SMF export reports
  today.

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

## Known compromises

These are deliberate for now and are where MIDI 2.0 work will start:

- **Channel events are MIDI 1.0 messages.** `ChannelEvent` wraps `ChannelMessage` (a status byte
  and 7-bit data), and project files store it as those bytes. Controllers, pitch bend, and pressure
  are therefore held at MIDI 1.0 resolution. That is lossless for every source Cadence reads today
  (SMF, MIDI 1.0 ports). Moving to protocol-neutral controller events needs project format version 2
  and a migration, and is planned together with MIDI 2.0 output.
- **Sixteen channels.** `MidiChannel` is 1 to 16. UMP adds 16 groups. The group belongs to routing
  (`TrackRoute`) and the endpoint, not to the music.
- **Ordering and chase are MIDI 1.0-shaped.** `EventPhase.BankSelect` (ADR 0003) and `ChaseState`
  work on CC 0 and CC 32 and program change messages. Both will follow the move to semantic
  controller events.
- **`ChannelMessage.CopyTo` is in the domain,** because project persistence uses it and
  `Cadence.Infrastructure` may not depend on `Cadence.Midi`.

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
