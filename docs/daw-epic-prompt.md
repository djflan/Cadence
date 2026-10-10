# Original task prompt: DAW architecture modernization epic

This is a verbatim copy of the prompt the epic was started with. It is the source of truth for
requirements. `docs/daw-epic-handoff.md` says what has been done and what is left; when the two seem to
disagree, this prompt wins. Delete this file with the handoff when the epic is finished.

---

# Cadence — DAW Architecture Modernization Epic

## Mission

You are the principal software architect and implementation engineer responsible for modernizing Cadence, a cross-platform, .NET-based MIDI sequencer evolving into a modern digital audio workstation (DAW).

This is an **implementation epic**, not merely a research or design exercise.

You are expected to:

1. Inspect the existing solution and understand its architecture.
2. Identify architectural limitations and technical debt.
3. Establish an implementation plan.
4. Refactor the existing architecture.
5. Implement the new architecture and associated workflows.
6. Preserve existing MIDI functionality.
7. Update project documentation and architecture decision records.
8. Add or update automated tests.
9. Validate the implementation.
10. Document any deferred functionality and remaining limitations.

You have autonomy over implementation details, provided you honor the architectural decisions documented below.

Do not simply introduce another architectural layer on top of the existing implementation.

Refactor, replace, or remove existing abstractions where appropriate.

**The objective is a cohesive, understandable, extensible architecture rather than an accumulation of compatibility wrappers and speculative abstractions.**

---

# 1. Project Background

Cadence began as a cross-platform MIDI sequencer inspired by Yamaha XGworks and traditional hardware-oriented MIDI sequencing applications.

Its original focus includes:

- MIDI sequencing and editing.
- Standard MIDI File import and export.
- External MIDI hardware.
- Yamaha XG.
- Roland GS.
- General MIDI and GM2.
- SysEx.
- Device profiles.
- MIDI automation and controllers.
- Multitimbral synthesizers.

Cadence is now evolving toward a modern DAW, drawing significant inspiration from Bitwig Studio.

However, Cadence must retain one important distinguishing characteristic:

**MIDI remains a first-class citizen, not a secondary feature attached to an audio workstation.**

This includes deep support for traditional hardware and manufacturer-specific MIDI extensions alongside modern software instruments.

Cadence should eventually support:

- VST3 instruments.
- VST3 MIDI/note effects.
- Audio effects.
- Software synthesizers.
- Audio routing and mixing.
- Reusable device chains.
- Advanced MIDI processing.
- Hybrid audio/MIDI workflows.
- MIDI 2.0.
- Out-of-process plugin hosting.

Not all of these capabilities need to be fully implemented within this epic.

However, the architecture established by this epic must provide a credible, extensible foundation for them.

## Existing Architecture

Cadence already contains valuable architectural concepts, including:

- A largely type-agnostic Track domain model.
- Polymorphic clip types.
- Immutable sequencing domain objects.
- Track routing abstractions.
- Device profiles.
- Protocol-specific MIDI handling.
- Playback-plan compilation.
- Automation lanes.
- Architecture decision records.

Preserve these strengths where appropriate.

Inspect the actual repository before making assumptions about the implementation.

The solution structure is approximately:

```text
Cadence/
├── src/
│   ├── Cadence.slnx
│   ├── SubModules/
│   └── <ProjectFolders>/
├── README.md
└── LICENSE
```

Follow existing repository conventions unless there is a compelling reason to change them.

---

# 2. Fundamental Design Principles

The new architecture follows several core principles.

## 2.1 Tracks Organize Musical Content

Tracks represent arrangement and musical intent.

They do not fundamentally represent MIDI endpoints, plugin instances, or audio mixer channels.

## 2.2 Clips Define Content

Clips define the content being arranged.

Examples include:

- Note clips.
- MIDI-oriented clips.
- Audio clips.
- Future clip types.

A track should not fundamentally depend on a particular clip format.

## 2.3 Devices Process Signals

Devices transform, generate, consume, or forward signals.

Examples include:

- MIDI processors.
- Software instruments.
- Hardware MIDI outputs.
- Audio processors.
- Plugin instruments.
- Plugin effects.

## 2.4 Signals Flow Through Devices

The normal device chain is sequential.

Each device receives the current signal state and produces the state consumed by the next device.

## 2.5 Automation Targets Devices

Automation is separate from sequential signal processing.

Automation addresses specific device instances and parameters.

It does not need to travel through preceding devices in a chain.

## 2.6 Routing Determines Signal Destinations

Routing determines where MIDI, note events, and audio travel.

Routing is independent of track identity and track role.

## 2.7 Mixer Channels Represent Audio Paths

Audio mixer channels are independent of arrangement tracks.

Multiple MIDI tracks may feed one instrument that produces one stereo audio output.

## 2.8 Presets Are Templates

Loading a device-chain preset creates independent instances.

Presets are not shared mutable processing objects.

## 2.9 Plugin Failures Must Not Crash Cadence

Third-party plugins must execute outside the main Cadence process by default.

A plugin crash must not terminate the application or corrupt its project state.

## 2.10 Prefer Simplicity

Use the simplest architecture that satisfies concrete requirements.

Do not build a full modular audio framework merely because one might eventually be useful.

---

# 3. Track Architecture

## Decision: Bitwig-Inspired Flexible Track Roles

Cadence will support recognizable track roles.

Conceptually:

```csharp
public enum TrackRole
{
    Instrument,
    Audio,
    Hybrid,
    Effect,
    Group
}
```

This is illustrative, not a mandatory implementation contract.

Track roles describe the user's workflow and expected processing configuration.

They must not unnecessarily constrain the underlying sequencing domain.

### Instrument Tracks

Primarily organize note-based musical content.

May:

- Host software instruments.
- Host MIDI effects.
- Route MIDI to other tracks.
- Route MIDI to shared instruments.
- Route MIDI to hardware.
- Exist without hosting an instrument directly.

### Audio Tracks

Primarily organize audio content.

May:

- Host audio effects.
- Receive audio from other tracks.
- Route audio to mixer channels and buses.

### Hybrid Tracks

May contain both note-based and audio-based content.

The processing architecture must accommodate both signal categories.

### FX Tracks

Host shared processing devices.

May receive signals from multiple tracks.

### Group Tracks

Organize tracks and support group-level processing and routing.

## Common Domain Model

Prefer retaining a common Track domain type.

Avoid unnecessary subclasses such as:

```text
InstrumentTrack
AudioTrack
HybridTrack
MidiTrack
```

unless a concrete requirement justifies them.

Track roles should primarily influence behavior, defaults, and presentation.

## Automatic Role Conversion

Cadence should behave similarly to Bitwig.

Track roles may change automatically as users add content or devices.

However, transitions must be safe.

For example:

```text
Instrument Track
    +
Audio Clip
    =
Hybrid Track
```

If the conversion is straightforward, perform it automatically.

If it would disrupt existing routing, processing, or device configuration, request confirmation or offer a compatible configuration.

Never silently discard:

- Clips.
- Devices.
- Automation.
- Plugin state.
- Routing.
- MIDI configuration.

Determine whether track roles should be persisted or derived from actual configuration.

Document the decision.

---

# 4. Clip Architecture

Preserve Cadence's existing clip-oriented arrangement model.

Conceptually:

```text
Track
├── NoteClip
├── NoteClip
├── AudioClip
└── Automation
```

Clips describe musical content.

They should not directly own MIDI endpoints or software instruments.

A track can contain different clip types without fundamentally changing its identity.

## MIDI Compatibility

Imported MIDI information must retain sufficient detail for faithful playback and export.

Preserve:

- Note information.
- MIDI channels.
- MIDI ports.
- Program changes.
- Controllers.
- Pitch bend.
- SysEx.
- Event timing.
- Event ordering.
- Manufacturer-specific information.

Standard MIDI File tracks may contain multiple MIDI channels.

Do not assume an imported MIDI track necessarily represents one channel.

Splitting MIDI content by channel should be a workflow option, not a destructive architectural requirement.

---

# 5. Device Architecture

## Decision: Unified Device Chains

Cadence will use Bitwig-inspired unified device chains.

Example:

```text
MIDI FX → Synth → Filter → Delay → Audio Output
```

Do not require users to manually manage independent MIDI and audio chains for ordinary workflows.

The device strip is the primary user-facing representation of processing order.

## Device Definition

A device definition identifies an underlying implementation.

Examples:

- Internal MIDI processor.
- Internal synthesizer.
- External MIDI instrument.
- VST3 instrument.
- VST3 MIDI effect.
- VST3 audio effect.

## Device Instance

A device instance represents a configured occurrence of a device.

It has:

- Stable identity.
- Device definition reference.
- Configuration.
- Parameter values.
- Runtime state.
- Bypass state.
- Persistence information.

Plugin identity and plugin-instance identity must remain distinct.

## Device Chain

A device chain is an independently identifiable collection of ordered device instances.

Conceptually:

```csharp
public sealed record DeviceChain
{
    public required DeviceChainId Id { get; init; }

    public required ImmutableArray<DeviceInstance> Devices
        { get; init; }
}
```

This is illustrative.

A device chain may also describe routing connections and exposed signal ports.

Keep declarative project state separate from runtime processing objects.

---

# 6. Device Chain Ownership

## Decision: Hybrid Ownership

Device chains are first-class entities.

They may be hosted by:

- Instrument tracks.
- Audio tracks.
- Hybrid tracks.
- FX tracks.
- Group tracks.
- Independent instrument racks.
- Independent processing racks.

Each chain instance has exactly one owner.

Multiple tracks may route signals into the same chain.

Ownership and signal routing must remain separate concepts.

For example:

```text
Piano ─────┐
Strings ───┤
Bass ──────┼──► XG Instrument Chain
Drums ─────┘             │
                         ▼
                    XG Softsynth
                         │
                         ▼
                     Audio FX
                         │
                         ▼
                       Mixer
```

This must use one actual XG synthesizer instance.

Do not duplicate the synthesizer for each source track.

---

# 7. Signal Processing Semantics

## Decision: Sequential Processing

The default device chain operates sequentially.

```text
Track Events
     │
     ▼
Device A
     │
     ▼
Device B
     │
     ▼
Device C
     │
     ▼
Output
```

Each device receives the previous stage's output.

A downstream device does not automatically receive an independent copy of the original track events.

Example:

```text
Three Notes
     │
     ▼
Arpeggiator
     │
     ▼
Twelve Generated Notes
     │
     ▼
Transpose
     │
     ▼
Twelve Transposed Notes
```

## Signal Passthrough

Devices generally preserve signal categories they do not process.

Examples:

- A MIDI processor normally passes audio through.
- An audio processor normally passes note events through.
- A synthesizer consumes note information and generates audio.
- An explicit filter may suppress selected events.

A device not understanding an event does not automatically mean that the event should disappear.

However, actual plugin behavior and capabilities must be respected.

Do not assume arbitrary VST plugins understand all MIDI messages.

## Host Adapters

Host adapters are responsible for converting between Cadence's internal signal representation and the interfaces exposed by plugins.

For example, a VST3 plugin may understand note events without supporting arbitrary MIDI SysEx messages.

The adapter must define how unsupported information is handled.

Preserve unrelated signals through host-managed passthrough where appropriate.

Do not silently destroy XG SysEx or controller information merely because a plugin does not understand it.

Explicit filtering remains supported.

## Runtime Considerations

A conceptual processor contract might resemble:

```csharp
public interface ISignalProcessor
{
    void Process(
        SignalBuffer input,
        SignalBuffer output);
}
```

This is illustrative.

Do not require allocating new managed objects or buffers for every processing stage.

Use efficient buffer management appropriate to real-time execution.

---

# 8. Automation Architecture

## Decision: Automation Is Separate from Signal Flow

Consider:

```text
[MIDI FX] → [Synth] → [Filter] → [Delay]
```

The track may contain automation for:

- MIDI effect rate.
- Synthesizer envelope release.
- Filter cutoff.
- Delay feedback.

The automation must target the relevant devices directly.

```text
Track Automation
      │
      ├──► MIDI FX / Rate
      │
      ├──► Synth / Envelope Release
      │
      ├──► Filter / Cutoff
      │
      └──► Delay / Feedback
```

Automation does not need to travel through the preceding devices.

## Requirements

Automation targets stable device-instance identities.

Parameter targets must not depend on device ordering.

Moving a filter before or after a synthesizer must not retarget its automation.

Automation must remain persistable and recoverable.

Missing plugins or parameters must not cause silent loss of automation data.

Plugin automation must use the appropriate plugin parameter model.

## MIDI Controllers Versus Parameter Automation

Distinguish:

**MIDI performance information**

Examples:

- MIDI CC.
- Pitch bend.
- Channel pressure.
- Program changes.
- SysEx.

These may travel through the musical signal pipeline.

**Device parameter automation**

Examples:

- VST filter cutoff.
- Synthesizer envelope release.
- Delay feedback.
- Compressor threshold.

These target particular device parameters.

Do not force device parameter automation to become MIDI messages.

## External Instrument Automation

For hardware instruments, parameter automation may eventually translate to:

- MIDI CC.
- RPN.
- NRPN.
- SysEx.

This translation belongs to the appropriate device adapter or interpreter.

## Timing

Automation scheduling must coordinate with signal processing.

For VST3, parameter changes should be delivered through the appropriate parameter-processing interfaces.

Avoid arbitrary UI-thread modifications of audio-thread plugin state.

---

# 9. Signal Routing

## Decision: Unified Routing Model

Cadence must support:

- Track-to-track MIDI routing.
- Track-to-instrument routing.
- Track-to-hardware routing.
- One-to-many routing.
- Many-to-one routing.
- Shared processing.
- Audio routing.
- Mixer buses.
- Routing from intermediate device outputs.

The underlying routing model must understand compatible signal types.

## Device Strip

The device strip remains primarily linear.

```text
[Arpeggiator] → [Transpose] → [Velocity]
      │              │
      │              └────► Track C
      │
      └───────────────────► Track B
```

Additional connections should appear as routing indicators attached to relevant devices.

Clicking an indicator opens the routing inspector.

The user should not need a visual node graph for ordinary routing.

## Routing Inspector

The inspector should expose:

- Input sources.
- Output destinations.
- Signal type.
- Device connection points.
- MIDI channel mapping.
- MIDI ports.
- Shared instrument destinations.
- Audio destinations.

## One Routing Model

The device strip and routing inspector must use the same underlying routing configuration.

Do not maintain separate, conflicting routing systems.

## Device Chain Connection Points

Device chains should expose inputs and outputs.

Intermediate device outputs may also be used as routing sources.

A chain may internally be represented as a simple processing graph, but ordinary linear processing should remain easy to understand.

## Feedback

Prohibit feedback cycles initially.

Detect invalid cycles and provide meaningful validation errors.

Do not create unbounded MIDI-processing loops.

---

# 10. MIDI Channel Routing

MIDI channels must not be treated as intrinsic track identities.

Distinguish:

- Original event channel.
- Destination channel.
- Device port.
- Routing connection.

A connection may:

- Preserve the original channel.
- Force a channel.
- Remap channels.
- Filter channels.

Example:

```text
Piano Track
    │
    ▼
Arpeggiator
    │
    ├──► Yamaha MU2000
    │       Port A
    │       Channel 4
    │
    └──► Software Instrument
            Channel 1
```

The underlying clips should not need modification merely to change destinations.

Preserve imported MIDI channel information.

Do not impose MIDI 1.0's sixteen-channel limitation on abstractions that must eventually support MIDI 2.0 groups and channels.

---

# 11. External MIDI Instruments

External MIDI instruments should be independently defined project entities.

Do not embed complete device identities and profiles redundantly in every track.

Example:

```text
Project
  │
  ├── External Instrument: Yamaha MU2000
  │      ├── MIDI Endpoint
  │      ├── Port Configuration
  │      ├── Device Profile
  │      └── Operating Mode
  │
  ├── Piano Track
  │      └── MU2000 / Port A / Channel 1
  │
  ├── Strings Track
  │      └── MU2000 / Port A / Channel 2
  │
  └── Drums Track
         └── MU2000 / Port A / Channel 10
```

Distinguish:

- Device identity.
- Physical endpoint.
- Logical port.
- Device profile.
- Supported MIDI standards.
- Current operating mode.
- Individual part assignments.

A device supporting XG and GS does not imply it is simultaneously operating in both modes.

## SysEx

Preserve raw SysEx during import, playback, editing, and export.

Do not automatically relocate imported initialization messages into global instrument configuration if doing so could change playback behavior.

Device-level initialization and part-level configuration should be distinguishable.

---

# 12. MIDI Event Representation

Cadence must continue to support:

- MIDI 1.0.
- Future MIDI 2.0.
- Note events.
- Controllers.
- Pitch bend.
- Program changes.
- Channel pressure.
- SysEx.
- XG and GS extensions.
- Manufacturer-specific information.

Prefer a rich internal musical-event representation with protocol-aware adapters where appropriate.

Do not unnecessarily encode everything as MIDI 1.0 messages early in the playback pipeline.

Do not destructively downgrade MIDI 2.0 information.

Preserve sufficient information for faithful round-trip MIDI import and export.

Do not conflate musical events with device parameter automation.

Avoid introducing a massive universal event hierarchy.

Use straightforward representations that solve concrete problems.

---

# 13. Mixer Architecture

## Decision: Mixer Channels Are Independent of Arrangement Tracks

The mixer represents audio signal paths.

For example:

```text
Piano ────┐
Strings ──┤
Bass ─────┼──► XG Softsynth ──► Mixer Channel 1
Drums ────┘

Lead ─────────► Vital ─────────► Mixer Channel 2

Pad ──────────► Surge XT ──────► Mixer Channel 3

                                 │
                                 ▼
                               Master
```

Twelve MIDI tracks feeding one stereo instrument do not require twelve audio mixer channels.

Requirements:

- Shared instruments.
- Audio buses.
- FX processing.
- Audio effects.
- Independent mixer channels.
- Future multi-output instruments.
- Hardware MIDI tracks without audio mixer channels.

Do not couple mixer-channel identity to track identity.

---

# 14. Device Chain Presets

## Decision: Presets Create Independent Instances

A saved chain is a template.

Loading it creates independent device instances.

Example:

```text
Saved Preset:
    Arpeggiator → Vital → EQ
```

Loading on two tracks results in:

```text
Track A
    Arpeggiator A → Vital A → EQ A

Track B
    Arpeggiator B → Vital B → EQ B
```

The instances must not share mutable state.

## Requirements

Support:

- Device ordering.
- Device configuration.
- Plugin state.
- Parameter values.
- Bypass state.
- Internal chain connections.
- Versioned serialization.
- Independent instance identities.

Distinguish between loading a preset and moving an existing chain.

Loading creates new identities.

Moving an existing chain preserves identity where appropriate.

External hardware references must be resolvable across machines.

---

# 15. Plugin Hosting Architecture

## Decision: Plugins Run Out of Process by Default

**Cadence must not load third-party plugins directly into the main application process by default.**

Third-party plugin hosting should use separate sandbox/worker processes.

The primary motivation is:

**A crashing plugin must not crash Cadence.**

This requirement applies particularly to VST3 instruments and audio effects.

The hosting architecture must also accommodate MIDI/note-processing plugins.

## 15.1 Process Architecture

Conceptually:

```text
┌───────────────────────────────────────────────┐
│               CADENCE HOST                    │
│                                               │
│  UI / Arrangement / Routing / Automation      │
│                                               │
│              Audio Engine                     │
│                    │                          │
│              Plugin Manager                   │
│                    │                          │
└────────────────────┼──────────────────────────┘
                     │
              IPC / Shared Memory
                     │
      ┌──────────────┼──────────────┐
      │              │              │
      ▼              ▼              ▼
┌──────────┐   ┌──────────┐   ┌──────────┐
│ Plugin   │   │ Plugin   │   │ Plugin   │
│ Worker A │   │ Worker B │   │ Worker C │
│          │   │          │   │          │
│ Vital    │   │ XG Synth │   │ Reverb   │
└──────────┘   └──────────┘   └──────────┘
```

These workers execute outside the main Cadence process.

A failure in one worker must not automatically terminate other workers or the main host.

## 15.2 Process Isolation Granularity

Prefer independent plugin-instance isolation where practical.

However, do not permanently hardcode one process per plugin instance.

The architecture should eventually support configurable isolation policies such as:

- One process per plugin instance.
- One process per plugin vendor or module.
- Multiple trusted instances in one worker.
- Optional in-process hosting for explicitly trusted plugins.

The default should prioritize stability.

Performance-oriented alternatives may be introduced later.

**Do not silently fall back to in-process hosting when sandbox initialization fails.**

## 15.3 Plugin Worker Responsibilities

A plugin worker should own:

- Plugin binary loading.
- Plugin instance creation.
- Plugin lifecycle management.
- Plugin processing.
- Plugin state restoration.
- Plugin parameter access.
- Plugin-specific thread requirements.
- Plugin cleanup.
- Plugin crash containment.

The main Cadence process should own:

- Project state.
- Arrangement.
- Routing configuration.
- Automation definitions.
- Device-instance identity.
- Worker lifecycle supervision.
- Plugin recovery policy.
- User-visible plugin status.

Runtime plugin objects must not become the authoritative representation of project state.

## 15.4 IPC Architecture

Use efficient inter-process communication.

Separate control-plane operations from real-time processing data.

### Control Plane

Examples:

- Create plugin instance.
- Destroy plugin instance.
- Load plugin state.
- Save plugin state.
- Enumerate parameters.
- Change configuration.
- Report errors.
- Monitor worker health.

A suitable local IPC mechanism may include:

- Named pipes.
- Unix domain sockets.
- Other platform-appropriate IPC transports.

### Real-Time Data Plane

Examples:

- Audio buffers.
- Note events.
- MIDI events.
- Timestamped parameter changes.
- Transport information.

Prefer shared memory where appropriate to avoid excessive audio-buffer copying.

Use a deterministic processing protocol.

Avoid ordinary synchronous RPC frameworks directly on real-time audio threads if they introduce unpredictable blocking or allocation.

Do not assume that shared memory automatically makes processing real-time safe.

The implementation must account for synchronization, buffer ownership, deadlines, and worker failure.

## 15.5 Audio Processing and Latency

Out-of-process hosting introduces unavoidable engineering considerations.

The architecture must account for:

- Audio-buffer exchange.
- Plugin processing deadlines.
- Worker synchronization.
- Buffer underruns.
- Plugin processing latency.
- Graph scheduling.
- Plugin delay compensation.
- Worker startup and shutdown.

Prefer a processing model that does not require the real-time audio callback to wait indefinitely on another process.

Explore suitable bounded processing, pipelining, and buffering strategies.

Document the latency tradeoffs.

Do not claim zero additional latency unless demonstrated.

## 15.6 Plugin Crash Recovery

If a plugin process crashes:

1. Detect the failed worker.
2. Mark affected plugin instances as unavailable.
3. Keep the Cadence application running.
4. Prevent invalid or stale shared-memory access.
5. Apply a defined output-failure policy.
6. Notify the user.
7. Preserve project state and plugin configuration.
8. Allow the plugin to be restarted or reloaded.

For instrument failures, silence is generally preferable to undefined output.

For audio effects, bypass may be appropriate when safe, but should not automatically be assumed.

The engine must avoid stale audio buffers and unexpected feedback or loud output during failure recovery.

Plugin recovery must not require closing the project.

Automatic restart may be configurable.

Avoid uncontrolled restart loops for repeatedly crashing plugins.

## 15.7 Plugin State Persistence

Plugin state belongs to the Cadence project model as persisted configuration data.

The plugin worker maintains the active runtime instance.

Cadence must be able to:

- Request plugin state snapshots.
- Persist plugin state.
- Restore plugin state.
- Recreate plugin instances after worker restart.
- Preserve automation mappings.
- Identify missing plugins.
- Report state restoration failures.

Do not assume a crashed plugin can provide its most recent unsaved internal state.

Maintain appropriate previously captured state for recovery.

## 15.8 Plugin UI Hosting

Plugin editors may require native windows or platform-specific GUI integration.

Design the hosting boundary so plugin UI responsibilities do not compromise main-process stability.

Evaluate:

- Separate plugin editor windows.
- Parent/child native window integration.
- Platform-specific window embedding.
- Focus and keyboard handling.
- DPI scaling.
- Worker termination while an editor is open.

Do not require complete cross-platform plugin-editor embedding during the initial architecture phase.

Document unsupported configurations.

## 15.9 Plugin Scanning

Plugin discovery should also be isolated from the main process.

A malformed or crashing plugin must not terminate Cadence during scanning.

Support:

- Plugin discovery.
- Plugin metadata caching.
- Plugin identity.
- Plugin compatibility information.
- Failed-plugin diagnostics.
- Optional plugin quarantine or blacklist.
- Rescanning.

Avoid repeatedly loading known-crashing plugins without user intervention.

## 15.10 Security Boundaries

Process isolation and operating-system security sandboxing are different concepts.

Running a plugin in another process provides crash isolation but does not automatically prevent filesystem, network, or other system access.

Do not describe process separation as a complete security boundary.

Future operating-system sandboxing may impose additional restrictions.

For this epic, prioritize crash isolation and robust process supervision.

Document the security limitations clearly.

---

# 16. Technology Strategy

## Decision: C#/.NET Is the Primary Language

Cadence is fundamentally a .NET application.

**C# is the default language for all new Cadence development.**

Use C# for:

- Domain modeling.
- Sequencing.
- MIDI processing.
- Application services.
- Project persistence.
- Routing configuration.
- Device-chain models.
- Automation.
- Playback orchestration.
- UI and application architecture.
- Plugin management.
- Process supervision.
- IPC control-plane implementation.
- General cross-platform functionality.

Do not introduce Rust merely because a component is performance-sensitive.

First determine whether modern .NET can satisfy the requirements.

Use profiling, benchmarking, and realistic performance targets.

## 16.1 Rust for Critical Native Components

When native code is genuinely justified, prefer Rust over C or C++ for new Cadence-owned implementations.

Possible uses include:

- Real-time audio processing.
- Low-level DSP.
- Native audio interfaces.
- Performance-critical shared-memory processing.
- Specialized low-latency IPC.
- Native plugin-hosting components.
- Platform integration that cannot be adequately handled through .NET.

**Rust should be a targeted implementation choice, not a second general-purpose application language.**

Prefer a small, cohesive native layer over a large parallel Rust application architecture.

## 16.2 C/C++ Policy

Avoid writing new Cadence-owned C/C++ code unless required by concrete constraints.

C/C++ remains acceptable for:

- Third-party libraries.
- Existing dependencies.
- Vendor SDK requirements.
- ABI compatibility.
- Thin interoperability shims.
- Platform-specific interfaces without reasonable alternatives.

Do not rewrite stable third-party C/C++ dependencies merely to eliminate C/C++ from the dependency tree.

Use existing libraries when they provide reliable functionality.

## 16.3 .NET and Rust Interoperability

Prefer stable interoperability contracts.

A versioned C ABI is an appropriate option where required.

Document:

- Memory ownership.
- Buffer ownership.
- Native handle lifetimes.
- Thread affinity.
- Error reporting.
- Resource disposal.
- Callback behavior.
- Synchronization.
- Version compatibility.

Do not expose arbitrary Rust-native object layouts directly across FFI boundaries.

Keep unsafe code isolated and justified.

## 16.4 Real-Time Performance

Critical processing paths should avoid:

- Unbounded allocations.
- Blocking locks.
- Disk I/O.
- Network I/O.
- Unbounded waits.
- Garbage-collection-sensitive operations.
- Unpredictable synchronization.
- Logging directly from real-time threads.

Rust does not automatically guarantee real-time safety.

Likewise, managed .NET is not automatically inappropriate for all real-time-adjacent work.

Select implementation technologies based on measured requirements.

## 16.5 Platform Support

Primary targets:

- macOS Apple Silicon.
- Windows x64.
- Windows ARM64 where practical.

Linux support is desirable when consistent with the existing project.

The plugin hosting architecture must respect platform and plugin binary architecture requirements.

For example, an ARM64 process cannot simply load an x64 plugin binary directly.

Where cross-architecture hosting is feasible, it requires appropriate compatible worker processes and supporting execution environments.

Do not assume universal plugin compatibility.

---

# 17. Playback Engine Modernization

The existing playback architecture uses immutable playback plans and MIDI-oriented compilation.

Preserve the strengths of immutable execution plans.

However, future processing must not depend exclusively on early MIDI 1.0 encoding.

Conceptually:

```text
Arrangement
     │
     ▼
Event Scheduling
     │
     ▼
Device Chains
     │
     ├──► MIDI Hardware
     │        │
     │        ▼
     │   Protocol Encoding
     │
     └──► Software Instrument
                  │
                  ▼
              Audio Engine
                  │
                  ▼
                Mixer
```

Requirements:

- Musical-event processing remains independent of transport encoding.
- Device processing supports stateful processors.
- Shared instruments receive appropriately merged and ordered events.
- Automation is scheduled against device parameters.
- Graph execution respects dependencies.
- MIDI events generated by plugins can be routed downstream.
- Device latency can be represented.
- Worker-process failures are handled safely.
- Real-time processing does not depend on the application UI thread.

Do not implement a complete production-quality audio engine merely as a side effect of the routing refactor.

Establish the appropriate boundaries and implement concrete functionality consistent with the existing repository.

Existing MIDI playback must remain functional throughout the transition.

---

# 18. User Experience

Cadence should feel like a modern DAW without hiding its advanced MIDI capabilities.

## Track Creation

Creating a track should not require selecting a MIDI endpoint or device profile.

Provide appropriate defaults.

## Device Strip

The device strip should support:

- Device insertion.
- Device removal.
- Device reordering.
- Device bypass.
- Device selection.
- Parameter inspection.
- Routing indicators.
- Chain preset saving.
- Chain preset loading.
- Plugin availability status.
- Plugin crash/recovery status.

## Routing Inspector

Provide straightforward access to:

- MIDI destinations.
- Audio destinations.
- Track-to-track routing.
- Channel mapping.
- Shared instruments.
- Hardware MIDI ports.
- Intermediate device connections.

## Plugin Failure UI

When a plugin fails, indicate the affected instance without making the entire application unusable.

Example:

```text
[MIDI FX] → [Vital: Crashed] → [EQ] → [Delay]
```

Provide appropriate recovery actions.

Avoid modal error storms or repeated disruptive notifications.

## Safe Operations

Track conversion, device movement, routing changes, and plugin replacement must not silently destroy existing content or automation.

---

# 19. Required Acceptance Scenarios

The architecture must support or establish concrete tested extension points for the following scenarios.

## Scenario 1: Multitimbral XG Synthesizer

Four MIDI tracks feed one shared XG synthesizer instance.

Each uses a different MIDI channel.

The synthesizer generates one stereo audio output routed to the mixer.

## Scenario 2: Track-Level MIDI Effects

A track contains a MIDI effect that transforms notes before sending them to a synthesizer.

## Scenario 3: Track-to-Track Routing

A MIDI effect generates events that another track receives.

## Scenario 4: Shared Processing

Multiple tracks route events into one shared MIDI processing chain.

## Scenario 5: Device Automation

A device chain contains:

```text
MIDI FX → Synth → Filter → Delay
```

Automation independently targets parameters on the MIDI effect, synthesizer, and filter.

Reordering devices does not change automation targets.

## Scenario 6: SysEx Preservation

A note-processing device does not unintentionally destroy unrelated XG SysEx or controller information.

## Scenario 7: Device Presets

Loading one saved chain onto multiple tracks creates independent device instances.

## Scenario 8: External MIDI Hardware

A track routes MIDI to an external Yamaha instrument using the correct port and channel.

## Scenario 9: Safe Track Conversion

Adding audio content to an instrument track can produce a hybrid track without destructive changes.

## Scenario 10: Routing Validation

Invalid signal connections and feedback cycles are detected.

## Scenario 11: Plugin Crash Isolation

A plugin worker crashes.

Cadence remains running.

The project state survives.

Affected plugin instances are marked unavailable.

The user can restart or reload the affected plugins.

## Scenario 12: Plugin State Recovery

A plugin worker is restarted.

Cadence restores the plugin using previously persisted state.

Automation references remain valid.

## Scenario 13: Multiple Plugin Workers

Several plugins execute in separate workers.

One worker fails.

Other unaffected workers continue operating.

## Scenario 14: Device Reordering

A device is moved within a chain.

Its stable identity and parameter automation are preserved.

Routing changes are validated.

## Scenario 15: Plugin Scanning Failure

A malformed plugin crashes during discovery.

The scanner worker fails, but Cadence remains operational.

The failure is reported and can be avoided on subsequent scans.

---

# 20. Implementation Phases

Implement this epic in coherent phases.

Do not stop after generating an architecture proposal.

## Phase 1: Repository Assessment

Inspect the existing solution.

Identify:

- Track and clip models.
- Current routing infrastructure.
- Playback architecture.
- Device profiles.
- Automation abstractions.
- Project persistence.
- MIDI integration.
- UI dependencies.
- Existing tests.
- ADRs.

Produce a concrete implementation plan.

## Phase 2: Domain Modernization

Implement the foundational architecture for:

- Track roles.
- Device definitions.
- Device instances.
- Device chains.
- Chain ownership.
- External instrument definitions.
- Routing connections.
- Automation targets.

Preserve existing sequencing functionality.

## Phase 3: Signal Routing and Processing

Implement:

- Sequential processing.
- Signal passthrough.
- MIDI routing.
- Shared instrument routing.
- Intermediate connection points.
- Routing validation.
- Updated playback-plan integration.

Preserve existing MIDI playback.

## Phase 4: Plugin Hosting Foundation

Implement or establish the concrete foundation for:

- Plugin worker processes.
- Worker lifecycle.
- Plugin discovery.
- Plugin instance identity.
- IPC contracts.
- Worker supervision.
- Crash detection.
- Recovery coordination.
- Plugin state persistence.

Evaluate the current repository's maturity before deciding how much actual VST3 processing can be completed safely.

Do not claim full VST3 support until functional plugin loading, processing, state handling, and recovery have been demonstrated.

## Phase 5: Real-Time Processing Boundaries

Establish the architecture required for:

- Audio-buffer exchange.
- Timestamped event exchange.
- Plugin parameter automation.
- Shared-memory IPC.
- Worker synchronization.
- Processing deadlines.
- Plugin latency reporting.
- Safe output handling after worker failure.

Use C# where appropriate.

Introduce Rust only when justified by actual runtime requirements.

## Phase 6: UI Integration

Update track configuration and routing workflows.

Implement the device-strip concepts appropriate to the existing UI framework.

Remove obsolete MIDI-only assumptions from the primary workflow.

## Phase 7: Persistence and Migration

Update project serialization.

Implement versioned migrations where required.

Preserve existing project data.

Introduce device-chain preset persistence.

## Phase 8: Testing and Documentation

Complete automated tests.

Validate builds.

Update architecture documentation.

Update ADRs.

Remove obsolete abstractions.

Document deferred functionality.

---

# 21. Documentation Requirements

Documentation updates are mandatory.

Review the existing documentation structure and follow established conventions.

At minimum, document:

## Architecture Overview

Explain:

- Arrangement.
- Tracks.
- Clips.
- Device chains.
- Routing.
- Automation.
- External instruments.
- Audio mixer.
- Plugin workers.

## Track Architecture

Document flexible roles and automatic conversion safeguards.

## Device Architecture

Document definitions, instances, chains, ownership, and processing semantics.

## Routing Architecture

Explain signal connections, shared instruments, and intermediate device routing.

## Automation Architecture

Explain why automation is separate from signal flow.

## MIDI Architecture

Explain:

- MIDI 1.0.
- MIDI 2.0 direction.
- SysEx.
- XG and GS.
- Device profiles.
- Protocol adapters.
- Channel routing.

## Plugin Hosting Architecture

Document:

- Worker processes.
- IPC.
- Shared memory.
- Plugin lifecycle.
- Crash recovery.
- Plugin state persistence.
- Isolation policies.
- Security limitations.
- Latency considerations.

## Native Technology Policy

Document:

- C#/.NET as the default.
- Rust for justified native and critical runtime components.
- C/C++ exceptions.
- Interoperability rules.

## ADRs

Create or update architecture decision records covering significant decisions.

Supersede obsolete ADRs explicitly.

Avoid contradictory documentation.

Update README.md to accurately describe the implemented architecture and future direction.

Clearly distinguish implemented functionality from planned functionality.

---

# 22. Testing Requirements

Add or update meaningful automated tests covering:

- Track roles.
- Track conversion.
- Device identity.
- Device-chain ordering.
- Chain ownership.
- Device presets.
- MIDI processing.
- Signal passthrough.
- Routing.
- Channel mapping.
- Automation targeting.
- External MIDI devices.
- Project serialization.
- Project migrations.
- Plugin worker lifecycle.
- Plugin crash detection.
- Worker recovery.
- Plugin state restoration.
- IPC failure handling.
- Existing MIDI playback regressions.

Prefer isolated domain tests where possible.

Use integration tests for routing, playback, and plugin hosting.

For plugin crash tests, deliberately terminate a test worker process and verify that Cadence survives.

Do not rely exclusively on mocked crash notifications.

Run available builds and test suites.

Fix introduced failures.

Document environmental limitations where tests cannot run.

---

# 23. Architecture and Engineering Standards

Follow these principles:

### Prefer Composition

Avoid excessive inheritance.

### Avoid Speculative Abstraction

Introduce abstractions when they solve actual requirements.

### Preserve Domain Immutability

Retain immutable project state where appropriate.

Allow mutable runtime objects where required.

### Separate Domain and Runtime State

A serialized device definition is not a live plugin instance.

### Stable Identity

Device identities, parameter targets, and routing references must not depend on list positions.

### One Source of Truth

The device strip, mixer, and playback engine must operate from one coherent project configuration.

### Maintainability

Favor clear naming, understandable components, and small cohesive responsibilities.

### Performance

Optimize measured bottlenecks.

Do not introduce native code without justification.

### Reliability

Plugin workers must not compromise project integrity.

### Compatibility

Maintain existing MIDI capabilities.

### Testing

Validate behavior rather than implementation details wherever practical.

---

# 24. Scope Management

This epic is an architectural modernization effort.

It should produce working improvements, not merely interfaces and documentation.

However, avoid expanding it into an attempt to build every DAW feature immediately.

The following may be deferred where appropriate:

- Complete production VST3 hosting.
- Full audio recording and editing.
- Advanced modulation.
- MIDI 2.0 implementation.
- MPE.
- Complex parallel instrument layering.
- Visual node-graph editing.
- Arbitrary feedback routing.
- Complete cross-platform plugin GUI embedding.
- Advanced plugin delay compensation.
- OS-level security sandboxing.

**Do not defer the core architecture required to support these capabilities.**

For deferred features, establish concrete and appropriately tested boundaries where necessary.

Do not create large speculative frameworks that provide no immediate value.

---

# 25. Definition of Done

This epic is complete when:

1. Cadence has a coherent DAW-oriented track architecture.
2. Tracks support flexible roles.
3. Device chains are first-class entities.
4. Device chains can be reordered and persisted.
5. Saved device chains create independent instances.
6. Routing is no longer fundamentally coupled to one MIDI output per track.
7. Shared instruments are represented correctly.
8. Automation targets device parameters independently of sequential signal flow.
9. External MIDI instruments are independently modeled.
10. Mixer channels are independent of arrangement tracks.
11. Existing MIDI sequencing remains functional.
12. Existing project data is preserved through supported migrations.
13. A concrete out-of-process plugin-hosting foundation exists.
14. Worker crashes cannot directly terminate the Cadence main process.
15. Plugin recovery behavior is implemented and tested to the scope delivered.
16. The implementation contains meaningful automated tests.
17. Documentation and ADRs reflect the resulting architecture.
18. Native technology guidance favors C# and uses Rust selectively.
19. Obsolete abstractions are removed or clearly deprecated.
20. Deferred capabilities are explicitly documented.

Do not represent architecture-only placeholders as completed functional capabilities.

---

# 26. Final Engineering Instructions

Work autonomously.

Inspect the repository before making architectural assumptions.

Produce an implementation plan and execute the epic.

Make reasonable implementation decisions without repeatedly requesting clarification.

Maintain a working application throughout the transition where practical.

Do not introduce unnecessary abstractions.

Do not overengineer the routing graph.

Do not sacrifice existing MIDI compatibility.

Do not introduce Rust without a concrete technical justification.

Do not load third-party plugins into the main Cadence process by default.

Do not assume process isolation provides a complete security boundary.

Do not mark unfinished capabilities as complete.

When finished, report:

1. Architectural changes.
2. Major components introduced, modified, or removed.
3. Completed workflows.
4. Plugin-hosting implementation status.
5. Documentation and ADR updates.
6. Build and test results.
7. Remaining limitations.
8. Deferred features.
9. Significant implementation decisions.

---

# 27. Long-Term Architectural Vision

Cadence should combine:

- The MIDI sequencing depth of Yamaha XGworks.
- The hardware compatibility of a dedicated MIDI workstation.
- The flexible device processing of Bitwig Studio.
- The automation and mixing workflows of a modern DAW.
- The reliability of crash-isolated plugin hosting.
- The portability and maintainability of modern .NET.
- The performance and safety advantages of Rust where native code is justified.

The goal is not to reproduce Bitwig feature for feature.

The goal is to develop a cohesive DAW architecture that makes sophisticated MIDI and audio workflows straightforward.

**Tracks organize music.**

**Clips contain musical content.**

**Devices process signals.**

**Signals flow through device chains.**

**Automation targets device parameters.**

**Routing connects processing components.**

**Mixer channels represent audio paths.**

**Plugins execute in isolated worker processes.**

**C# remains the primary development language.**

**Rust is preferred for justified native runtime components.**

**MIDI remains a first-class citizen.**

Above all:

**Cadence should remain understandable, maintainable, reliable, and enjoyable to develop.**
