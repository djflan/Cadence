# DAW architecture epic: handoff

Read this before continuing the epic. The original requirements are in `docs/daw-epic-prompt.md` (the source of truth;
sections 19, 22, 25, and 26 matter most). The plan and assessment are in `docs/daw-architecture-plan.md`; the result
is described by `docs/architecture.md`, `docs/plugin-hosting.md`, `docs/project-format.md`, and ADRs 0021 to 0027.
These `daw-epic-*` files can be deleted once the owner accepts the epic.

Branch: `claude/busy-gates-qengpq` (pushed). Do not merge to `main`; do not open a PR unless asked.

## 1. Status

Every phase of the epic has been implemented and tested to the scope stated in the docs. What is modelled but not
functional (no audio engine, no third-party plugin formats) is labelled as such everywhere.

| Area | State |
| ---- | ----- |
| Domain: roles, devices, chains, instruments, connections, mixer, device automation | Done, tested |
| `Cadence.Signal`: chain runner, built-in devices, signal graph | Done, tested; a passthrough chain allocates 0 bytes after warm-up |
| Routing replacement (`TrackRoute`, `RoutingTable`, `ResolvedRoute`, `PlanTrackBinding` removed) | Done; format 3 fixtures send byte-identical MIDI (section 5) |
| Format 4, 3→4 migration, chain preset files (`cadence-chain-preset` v1) | Done, tested; roles reconciled on load |
| Commands (devices, presets, racks, connections, instruments, mixer, roles, groups), refusal of routing errors | Done, tested |
| UI: Track (role), Devices (strip), Connections (routing inspector), audio clips drawn, plugin status and Restart | Done; view-model tests; **the app was run on macOS** and the sections checked visually |
| UI: Racks (new, rename, edit in the strip, remove), Mixer (channels: name, gain, pan, mute, solo, output, remove; master gain) | Done; view-model tests; run on macOS: new rack → strip edits it → back; channel added; fader drag then one Undo restored 0 dB |
| Undo merging: same merge key within 1 s is one undo step (parameter sliders, mixer gain/pan, master gain) | Done; `EditHistory` tests with a manual `TimeProvider`, view-model tests |
| Plugin hosting (`Cadence.Plugins.Protocol`, `Cadence.Plugins`, `Cadence.PluginWorker`) | Done, merged; real worker processes killed in tests; reference plugins only |
| Application bridge (`PluginDeviceHost`): devices ↔ worker instances, state into the project, status, restart | Done, tested with real workers; wired into the desktop app |
| Plugin MIDI effects in the plan (ADR 0028): rendered in their worker at compile time, routed downstream | Done; `PluginMidiInThePlanTests` with real workers (transpose to a port, automation, tap, killed and hung workers) |
| Acceptance scenarios 1–15 | All have tests: table in `docs/architecture.md` and `docs/plugin-hosting.md` |
| Docs: ADRs 0021–0028, architecture, project format, MIDI files, plugin hosting, README | Done |
| Rust | Not introduced: nothing measured needs it yet (ADR 0026 sets the bar) |

**Last full run (macOS arm64, SDK 10.0.300):** build 0 warnings, 0 errors; `dotnet format --verify-no-changes` exit 0;
`dotnet test src/Cadence.slnx`: 1132 total, 1119 passed, 12 skipped (other platforms' adapters), 1 failed: the
intermittent `CoreMidiProviderTests.Playback_ThroughCadenceVirtualPort_ArrivesInOrder`, which also fails on the
untouched base (section 2). Plugin integration tests: 8 consecutive passes of 27, no workers left behind; the bridge
and strip tests (5) pass.

## 2. Build environment

Use the README commands on a machine with SDK 10.0.300 (`global.json`). One test project:
`dotnet test --project src/Cadence.Tests.Unit/Cadence.Tests.Unit.csproj -- --filter-namespace "Cadence.Tests.Unit.Signal"`.

- `CoreMidiProviderTests.Playback_ThroughCadenceVirtualPort_ArrivesInOrder` fails intermittently on macOS (real
  CoreMIDI timing; it failed before any change). Two other CoreMIDI tests timed out occasionally under full-suite load
  during the plugin work. Not a regression; do not chase it as part of the epic.
- Plugin tests that make a worker call `Environment.FailFast` leave macOS crash reports in
  `~/Library/Logs/DiagnosticReports` (33 after this session). Harmless; delete them if they bother you.
- CI (`.github/workflows/ci.yml`) runs only on `main` and pull requests, so **nothing has been built on Windows or
  Linux** in this epic.
- To look at the UI: `./src/Cadence.Desktop/bin/Debug/net10.0/Cadence.Desktop path/to/project.cadence` (the app opens a
  project or MIDI file named on the command line).

## 3. Analyzer traps already hit (warnings are errors)

- `CA2208`: `new ArgumentException(msg, nameof(X))` must name a *parameter of the enclosing method*; `nameof(Property)` is only allowed inside the property's own accessor.
- `CA1711`: an enum member cannot end in `Ex` (`SysEx` became `SystemExclusive`).
- `IDE0005`: unused `using` is an error, including in tests.
- `ImmutableArray<T>` has no `FindIndex`; use a loop. With xUnit, comparing a collection expression to an `ImmutableArray` may need an explicit `Assert.Equal<T>(...)`.
- Positional records that re-declare their properties are awkward; use an explicit constructor (as `ProfileReference` does).
- Style: file-scoped namespaces, braces always, `_camelCase` fields, XML docs only where intent/units/ownership are not obvious, tests named `Subject_Condition_Expectation`. Keep test assertions plain; two bad tests came from over-clever expressions.
- `Cadence.Domain` may reference only the BCL. Add every new project to `DependencyDirectionTests`.
- `CA1711`: type names cannot end in `Stream` either (the feeds are `ExternalPartFeed`, `SoftwareInstrumentFeed`, `ParameterFeed`).
- `CA1822`: a property returning a static (`=> ChannelOption.All`) must be an initialized auto-property to bind from AXAML.
- Namespaces: in `Cadence.Tests.Unit`, `Domain.Routing.X` resolves to the test namespace; import `Cadence.Domain.Routing` instead. In `Cadence.Application.Plugins`, `Plugins.Protocol.X` resolves to the Application namespace; use `using` aliases.
- `EventOrder.Compare` boxes `EventPhase` (`Enum.CompareTo(object)`); on hot paths compare `(int)phase`.
- `dotnet format` reorders imports after edits; run `dotnet format src/Cadence.slnx` before `--verify-no-changes`.

## 4. What exists, and the decisions behind it (do not re-decide these)

Where things are: `docs/architecture.md` ("Projects and their references", "Where does this belong?", and "Tracks,
devices, and routing"). Plugin hosting: `docs/plugin-hosting.md`, including the application bridge.

### Decisions
1. **Role is persisted and reconciled** with content (it cannot be derived for empty/Effect/Group tracks). `Track` never rejects clips because of role. Effect/Group refuse clips; narrowing that would hide content is refused; conversion never discards.
2. **One routing model**: the `SignalConnection` list. Sources: track output, rack output, device tap. Destinations: track/rack input, external-instrument part (port + channel via mapping), mixer channel, master. The strip and the inspector must both edit this list only.
3. **Chains are first-class**, one owner each, addressed by `DeviceChainId`; a track has at most one chain (optional, so `project with { Sequence = ... }` keeps working).
4. **External instruments are project entities**; the profile is stated once per instrument. There is **no** "external instrument device kind": it would be a second way to express a route.
5. **Passthrough** is by `EventClass`: a device is given only the classes in `Handles`; everything else is merged back in canonical order. Only explicit filter devices drop events (SysEx/XG data must survive note processors).
6. **Processing runs at plan-compile time** for built-in processors (immutable plans stay; the real-time thread only dispatches). Plugins process in workers at runtime. Software-instrument event streams are produced but **not played** (no audio engine): emit a diagnostic, never imply support.
7. **Device automation is never MIDI**: it becomes `ParameterChange`s. SMF export reports device lanes it cannot write (SMF204).
8. **Presets are `DevicePreset` templates without ids**, so loading always creates new identities; presets exclude connections (no hardware references).
9. Old `TrackRoute.Transpose` migrates to a **Transpose device** in the track's chain; `Voice` moves onto the connection; `Channel` becomes `ChannelMapping.Force`; one `ExternalInstrument` per distinct (endpoint, profile) pair, including an "Unassigned" one for routes that have settings but no endpoint (the old code kept those).
10. `TrackRendering.Render(track, ppqn, channelOverride)`: the override is the **unique Force channel** of the track's outgoing event connections, else none. This preserves the old "two lanes collide after the channel override" behaviour.
11. **Mute and solo apply to a track's own content** (the old rule: solo overrides mute). Events routed *into* a muted track from a playing one still pass through its chain. Revisit only if the UI needs "mute silences the track's output".
12. **Device automation is not signal**: it is rendered for every track, playing or muted, applied to built-in processors at its ticks, and returned as `ParameterFeed`s for every other device. A lane whose device is missing is reported (`AutomationTargetMissing`) and kept.
13. **At compile time, a plugin MIDI effect runs in its own worker (ADR 0028)**: a `RenderEvents` request runs a fresh copy from the project's stored state and parameters over the timeline (sample frames on the tempo map), and its output joins the chain. If its worker is gone, hung, or failing, or the request is over the protocol's limits, its events pass through unchanged with a diagnostic. An unknown device always passes events through, with a diagnostic. An instrument without an in-process processor takes its `Handles` classes out of the chain (this matches `SignalPresence.Through`); SysEx and other unhandled classes go on to the track's connections.
14. **The evaluator never fails on bad routing.** It leaves out every connection named in a validator error, reports it (`InvalidConnection`), and as a backstop drops any connection that still closes a loop during topological ordering (`Feedback`). Audio connections are validated and reported but not followed (no audio engine).
15. **"Not routed"** is reported for a playing track with no outgoing event connection (from the track or a tap on its devices) and no instrument in its chain, whatever its content, like the old compiler. With an instrument but no connection, it is reported only when events remain after the chain (for example SysEx the instrument does not handle). A tap on a chain whose owning track is gone is left out and reported (it can never run).
16. **Arpeggiator phrases are anchored at the first note** played while nothing is held, including after a gap shorter than a step; then they step every rate interval while anything is held, so every phrase sounds (a legato note, starting exactly where the last one ends, continues the phrase). Held pitches are deduplicated and played lowest first; generated notes take the source note's channel, velocities and ordering key, and an `EventId` derived from the source note's ID and the step's tick. Output, IDs included, is identical across evaluations and whether processed in one block or many (tested).
17. **Language policy (owner clarification, 2026-10-10; overrides any "C# only, no Rust" wording in earlier task instructions or the plugin spec).** C#/.NET is the default for all code. Rust is the choice for performance-critical or native sections, and replaces C/C++ for new Cadence-owned native code; C/C++ only for third-party libraries, vendor SDK requirements, and thin ABI shims. Likely Rust candidates: the plugin worker's native VST3 hosting layer, the real-time shared-memory data plane and audio callback path, and DSP once an audio engine exists. Each needs a stated reason (a measurement or a native ABI constraint), a small versioned C ABI to .NET (prompt section 16.3), and must not become a second application language. This is the same as prompt section 16.
18. **The inspector's fields are a view of the routing model** (`TrackOutputs`): the instrument is reused per (endpoint,
    profile); the primary connection is the track's first event connection to an external instrument (its channel
    filter is kept); transposition lives in the chain's first Transpose device, added at the end when needed, and is
    only rewritten when it changes. Format 3 migration goes through the same `Write`.
19. **Plan order and slots**: parts are ordered by source track (taps count as their chain's track, racks last), then
    connection order; each distinct available endpoint is a slot in that order. The compiler sorts by
    (tick, phase, origin, sequence, part). Voice selection is an initial event (origin = source track index, or -1 for
    a rack; sequence -1) on the forced channel, else the source track's first channel mapped by the connection, else 1.
20. **"Not routed"** at plan level is also reported for a playing track whose only external parts are unavailable, unless
    it has a software instrument or another event destination.
21. **Live notes** (audition, thru, on-screen keyboard, metronome, instrument setup) follow the nearest playable
    instrument through connections (breadth-first), with the last forced channel and the Transpose devices on the way.
22. **Edits that add a routing error are refused** (`CommandRefusedException`, shown as a message by
    `MainViewModel.Execute`): device moves, bypass, inserts, connections, instrument and mixer edits, role changes,
    adding audio, and inspector output edits. Events that would land inside an audio clip are refused; a recording
    that hits this keeps its take and says why it was not added.
23. **Connection IDs are unique** in `Project` (a file listing one twice is a format error).
24. **Plugin devices** have definition IDs `plugin:<format>:<module>/<plugin>`; available plugins join the catalog as
    data with the parameters their worker reports. State is captured into the project before every save (not on
    autosave). Removing a device stops its worker.
25. **Undo merging** is opt-in per command: `IProjectCommand.MergeKey`. Consecutive commands with the same key within
    `EditHistory.MergeWindow` (1 s, measured with an injectable `TimeProvider`) replace the top undo step's result and
    keep its "before" state. Undo, redo, and reset end the run. Keys: `parameter:{device}:{parameter}`,
    `gain:{channel}`, `pan:{channel}`, `master-gain`.
26. **`MainViewModel.RefreshAsync` coalesces**: a refresh asked for while one runs sets a flag and the running one goes
    round once more. (It used to re-post itself, which spins the UI queue, and overflows the stack with an inline
    test dispatcher.) `PlaybackController` evaluates the graph on the thread pool only when a device will be rendered
    out of process, so refreshes without plugin MIDI effects keep their old synchronous behaviour.

## 5. Regression net (hold the refactor to these)

Asserted by `Format3EquivalenceTests` through the **new** pipeline (migration, signal graph, routing preparation, compiler). Frozen from the **old** pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with every endpoint present except `gone-*` (missing) and `old-*` (present as `new-*` with the same name, so bound by name), profiles from `/profiles`:

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`: Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override. `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must stay green unchanged. The fixtures live in `src/Cadence.Tests.Unit/Infrastructure/Fixtures/` and must never be regenerated.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`; initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 6. Not verified

- Windows and Linux (no CI run on this branch; WinMM/ALSA paths, worker copying, and timing there are untested).
- Real-time behaviour of out-of-process audio under a real audio device (there is no audio engine); plugin latency
  and deadline handling are tested only with the reference plugins and a test clock.
- Any third-party plugin: no VST3 or other format is loaded; claims are limited to the reference plugins.
- The UI was run and inspected on macOS with a hand-made format 4 project (role, chain with a tap and a missing plugin,
  audio clip), and the Racks and Mixer sections with `format3-routing-full.cadence` (synthetic clicks and drags; an
  autosave restore brought the new rack back). Clicking through every workflow was not done by hand; view-model tests
  cover the workflows. There are no Avalonia headless UI tests.
- Plugin MIDI rendering is tested with the reference Transpose plugin only, at the routing level with real workers.
  The app's "recompile when a plugin MIDI effect's status changes" wiring has no UI-level test (the plugin strip tests
  use an inline dispatcher that runs posted work on worker threads; a headless Avalonia test would be the right place).

## 7. Remaining work, in priority order

1. **Audio engine** (deferred by the prompt; listed in `docs/architecture.md` "Deferred"): play audio clips and
   software-instrument feeds, mixer gain/pan, and drive plugin instances from playback. ADR 0026's measurement bar
   decides whether the callback is C# or Rust.
2. **Third-party plugins** (deferred): a native hosting layer behind `IHostedPlugin` in the worker (Rust per ADR 0026),
   parameter enumeration, editors.
3. **CI on all platforms**: open a PR (when the owner asks) so the matrix builds Windows and Linux.
4. **Avalonia headless UI tests** (`Avalonia.Headless.XUnit`) for the inspector sections, so the UI is checked without a
   display (also what a cloud agent on Linux would need).
5. Smaller: rescanning plugins from the UI; translating device automation to CC/RPN/NRPN/SysEx for hardware; group
   track processing; a dedicated mixer view (the inspector section is a list, not a console with meters).
