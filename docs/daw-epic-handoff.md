# DAW architecture epic: handoff

Written mid-work so another agent can continue. Delete this file (and the two `daw-epic-*` files below) when the epic is done.

## Read these first, in this order

1. **`docs/daw-epic-prompt.md`**: the original task prompt, verbatim. It is the source of truth for requirements, scenarios 1 to 15 (section 19), the testing list (section 22), the definition of done (section 25), and the final report format (section 26). Where this handoff and the prompt disagree, the prompt wins.
2. `docs/daw-architecture-plan.md`: repository assessment, decisions, phases, and the frozen baseline hashes.
3. This file: what is done, what is left, and the traps already hit.
4. `docs/daw-epic-plugin-hosting-spec.md`: the concrete spec for the plugin-hosting work package (phases 4 and 5).

Branch: `claude/busy-gates-qengpq`. Do not open a PR unless asked.

## 1. Honest status

| Area | State |
| ---- | ----- |
| Repo assessment, plan | Done, committed. |
| Format 3 fixtures + pre-refactor hashes | Done, committed. See section 5. |
| Domain model (`Cadence.Domain`) | Done and tested. |
| Domain tests | Done. 768 unit tests at the end of that step (691 baseline + 77 new). |
| `Cadence.Signal` project | **Done and tested** (section 4, "Cadence.Signal"). Nothing outside the tests calls it yet: playback still compiles from `TrackRoute`. **Whole solution builds with 0 warnings; 838 unit tests pass** (768 + 69 signal tests + 1 dependency-direction row), stable over repeated runs; integration 44 total, 12 platform-skipped on macOS arm64, and 0 failed apart from the intermittent CoreMIDI test in section 2; `dotnet format src/Cadence.slnx --verify-no-changes` exits 0. |
| Replacing `TrackRoute` / `RoutingTable` | Not started. `Project.Routing` still exists on purpose. Nothing uses the new `Chains`, `Connections`, `Instruments`, or `Mixer` yet. |
| Persistence format 4 + migration | Not started. The serializer does not write or read any new type (roles, audio clips, devices, connections, device automation lanes would currently throw or be lost on save). |
| Application commands, Presentation, Desktop UI | Not started. |
| Plugin hosting | **Incomplete, uncommitted, unmerged, never compiled by me.** See section 6. |
| ADRs, architecture docs, README, CI | Not started (only the plan and this handoff exist). |
| Rust | Deliberately not introduced. Nothing measured needs it. |

Nothing here is a finished capability. Do not describe any of it as complete in the docs. In particular, saving a project that contains a new-model entity is **not** supported yet.

## 2. Build environment (non-obvious, costs time if missed)

**On a machine with the pinned SDK (10.0.300), none of the workarounds below apply.** The `Cadence.Signal` step ran on macOS arm64 with SDK 10.0.300 using the plain README commands: `dotnet build src/Cadence.slnx`, `dotnet test src/Cadence.slnx`, `dotnet format src/Cadence.slnx --verify-no-changes`. For one test project: `dotnet test --project src/Cadence.Tests.Unit/Cadence.Tests.Unit.csproj -- --filter-namespace "Cadence.Tests.Unit.Signal"`. There, `CoreMidiProviderTests.Playback_ThroughCadenceVirtualPort_ArrivesInOrder` fails intermittently: it failed on the untouched branch **before any change**, then in about 4 of 6 later runs. It drives the real CoreMIDI service with real timing (CONTRIBUTING.md calls the CoreMIDI tests unreliable) and uses no changed code. It is not a regression; do not chase it as part of the epic.

The rest of this section applies only to the earlier Linux sandbox (SDK 10.0.112):

- `dotnet` was installed from Ubuntu apt: SDK **10.0.112**. `global.json` pins **10.0.300**, so `dotnet` run from inside the repo fails ("compatible SDK not found"). Do not edit `global.json`.
- **Run every dotnet command from `/tmp` with absolute paths**: `cd /tmp && dotnet build /home/user/Cadence/src/Cadence.slnx --no-restore` (restore once: `dotnet restore /home/user/Cadence/src/Cadence.slnx`).
- `global.json` also selects the Microsoft.Testing.Platform runner, which does not apply from `/tmp`. **Run the test executables directly:**
  `/home/user/Cadence/src/Cadence.Tests.Unit/bin/Debug/net10.0/Cadence.Tests.Unit` (xUnit v3; filters `-method "Ns.Class.Method"`, `-class`, `-namespace`). Same for `Cadence.Tests.Integration`.
- **`dotnet format` cannot run against the repo**: it starts its own build host, which reads the repo's `global.json` and fails. Run it on a scratch copy whose `global.json` is relaxed:
  `tar --exclude=bin --exclude=obj --exclude=.git --exclude=.claude -cf - . | tar -xf - -C $COPY`, `sed -i 's/10.0.300/10.0.112/' $COPY/global.json`, restore, then
  `cd /tmp && dotnet format $COPY/src/Cadence.slnx --no-restore --verify-no-changes`. Check its **own** exit code (a `| tail` hides it). `rsync` is not installed.
- Outbound: nuget.org, archive.ubuntu.com, pypi work. `dot.net` and `builds.dotnet.microsoft.com` return 403.
- Linux, headless: the Avalonia UI cannot be launched here. `Cadence.Desktop` can only be built, and its compiled bindings are checked at build time.
- **Never `git add -A` blindly.** `.claude/worktrees/` holds the plugin agent's worktree; it is excluded in `.git/info/exclude` of this clone only. Add paths explicitly. Use `git -C /home/user/Cadence ...` from scripts: a `cd` into the worktree changes the reported working directory.
- Pushing needed GitHub access to be reconnected once (a 403 `repo_not_connected` earlier); it works now. If it fails again, `check_repo_access` explains why.

## 3. Analyzer traps already hit (warnings are errors)

- `CA2208`: `new ArgumentException(msg, nameof(X))` must name a *parameter of the enclosing method*; `nameof(Property)` is only allowed inside the property's own accessor.
- `CA1711`: an enum member cannot end in `Ex` (`SysEx` became `SystemExclusive`).
- `IDE0005`: unused `using` is an error, including in tests.
- `ImmutableArray<T>` has no `FindIndex`; use a loop. With xUnit, comparing a collection expression to an `ImmutableArray` may need an explicit `Assert.Equal<T>(...)`.
- Positional records that re-declare their properties are awkward; use an explicit constructor (as `ProfileReference` does).
- Style: file-scoped namespaces, braces always, `_camelCase` fields, XML docs only where intent/units/ownership are not obvious, tests named `Subject_Condition_Expectation`. Keep test assertions plain; two bad tests came from over-clever expressions.
- `Cadence.Domain` may reference only the BCL. Add every new project to `DependencyDirectionTests`.

## 4. What exists, and the decisions behind it (do not re-decide these)

### Committed in `src/Cadence.Domain` (all tested)
- `Devices/`: `DeviceId`, `DeviceChainId`, `ParameterId(uint)`, `DeviceDefinitionId`, `DeviceReference`, `DeviceDefinition` (declarative: `Consumes`, `Produces`, `Handles`, parameters), `ParameterDescriptor`, `DeviceInstance` (stable id, parameters, bypass, `PluginState`), `DeviceChain` (+ `ChainOwner`: Track or Rack), `DevicePreset`/`DeviceChainPreset` (templates with **no ids**), `SignalKinds`, `DeviceDefinitionLookup`.
- `Sequencing/`: `TrackRole`, `AudioClip` + `AudioSource` (metadata only, no audio engine), `EventClass` (flags: Notes, Controllers, Programs, SystemExclusive, Meta). `Track` gained `Role` and `Group`. `AutomationTarget` gained a device-parameter variant (`ForDevice`, `IsMidi`). `TrackRendering` returns `RenderedTrack.ParameterChanges` separately from MIDI events. `ControlValue.FromFraction/ToFraction`.
- `Routing/`: `ExternalInstrument` (+ `ExternalPort`; ports carry endpoints), `ChannelMapping` (Only, Force, Remap; filter runs before mapping), `SignalNode`, `SignalConnection` (kind, source, destination, mapping, voice), `SignalPresence`/`SignalFlow`, `SignalRoutingValidator` (existence, kind fit, "nothing to carry", cycles, duplicates, automation warnings).
- `Mixing/Mixer.cs`, `Projects/Project.cs` (new `Instruments`, `Chains`, `Connections`, `Mixer` + lookups), `Projects/TrackRoleConversion.cs` (plans: `Automatic` / `NeedsConfirmation` / `Refused`; `Reconcile` only widens).
- Tests: `Cadence.Tests.Unit/Domain/{Devices,Routing,Sequencing}`, shared `TestDevices.cs` (MIDI FX, Synth, Filter, Delay definitions) and `Domain/Routing/RoutingFixture.cs` (project builders).

### `src/Cadence.Signal` (all tested; references `Cadence.Domain` only)
- `SignalEvent(TrackEvent Event, int Origin, int Sequence)`: an event plus its ordering key. `Origin` is the source track's index in the sequence and `Sequence` its index among that track's `TrackRendering.Render` events (use negative indexes for initial voice events). `SignalOrder.Compare` is (tick, phase, origin, sequence), the plan compiler's order, so the compiler in item 2 can sort by these keys and keep the section 5 hashes. Devices keep the key of the input an output comes from (`SignalEvent.With`). `SignalOrder` compares phases as `int`, because `EventOrder.Compare` boxes the enum (it calls `Enum.CompareTo(object)`).
- `SignalBuffer`: reusable growable array; `SignalBlock`: half-open tick window (`SignalBlock.Everything` for plan compilation).
- `ISignalProcessor`: `SetParameter`, `Process(in SignalBlock, ReadOnlySpan<SignalEvent>, SignalBuffer)`, `Reset`. It works on blocks, so the same contract can serve a real-time runner later; at compile time one block covers the whole timeline. `IReportingProcessor.TakeReport()` for things like dropped notes.
- `DeviceCatalog`: definitions plus in-process factories. `DeviceCatalog.BuiltIn` = Transpose (`cadence.midi.transpose`), Event Filter (`cadence.midi.event-filter`), Arpeggiator (`cadence.midi.arpeggiator`). `With(plugin, factory)` **throws**: plugins are data only and never run in process. Each built-in has `CreateInstance(...)` (use `TransposeProcessor.CreateInstance(n)` for the decision 9 migration).
- `ChainRunner(chain, catalog, ppqn)`: stage kinds `Processor`, `Instrument` (events in, no events out, no in-process processor: its `Handles` classes leave the chain through `IChainObserver.InstrumentInput`), `Transparent` (bypassed, audio-only, unknown definition, or an event plugin: passes through, with a `DeviceUnavailable` or `NotProcessedHere` diagnostic). Parameter changes split the block at their tick, apply before events at the same tick, and still update a bypassed processor. `IChainObserver.AfterDevice` provides tap outputs. A passthrough chain allocates 0 bytes per run after warm-up (tested with `GC.GetAllocatedBytesForCurrentThread`).
- `SignalGraph.Evaluate(project, catalog)` returns `SignalGraphResult`: `ExternalParts` (one `ExternalPartFeed` per event connection to an external instrument: resolved port id, `ForcedChannel`, the unresolved `Voice`, mapped events), `SoftwareInstruments` (one feed per instrument device, all sources merged), `Parameters` (device automation for devices **not** processed in process), `Diagnostics`. `SignalGraph.ChannelOverride` implements decision 10; `SignalGraph.Map` applies a `ChannelMapping`. Diagnostic texts for "not routed", automation replacement, lane dropping and transposed-away notes reuse the old `PlanDiagnostic` wording.
- Tests: `src/Cadence.Tests.Unit/Signal/` (`SignalFixture` has project builders and a real `RecordingProcessor`). Scenarios 2, 3, 4, 6 and 10 are in `SignalGraphTests`; passthrough, bypass, sequential processing and zero allocation are in `ChainRunnerTests`.

### Decisions
1. **Role is persisted and reconciled** with content (it cannot be derived for empty/Effect/Group tracks). `Track` never rejects clips because of role. Effect/Group refuse clips; narrowing that would hide content is refused; conversion never discards.
2. **One routing model**: the `SignalConnection` list. Sources: track output, rack output, device tap. Destinations: track/rack input, external-instrument part (port + channel via mapping), mixer channel, master. The strip and the inspector must both edit this list only.
3. **Chains are first-class**, one owner each, addressed by `DeviceChainId`; a track has at most one chain (optional, so `project with { Sequence = ... }` keeps working).
4. **External instruments are project entities**; the profile is stated once per instrument. There is **no** "external instrument device kind": it would be a second way to express a route.
5. **Passthrough** is by `EventClass`: a device is given only the classes in `Handles`; everything else is merged back in canonical order. Only explicit filter devices drop events (SysEx/XG data must survive note processors).
6. **Processing runs at plan-compile time** for built-in processors (immutable plans stay; the real-time thread only dispatches). Plugins process in workers at runtime. Software-instrument event streams are produced but **not played** (no audio engine): emit a diagnostic, never imply support.
7. **Device automation is never MIDI**: it becomes `ParameterChange`s. SMF export must report (not silently drop) device lanes. `SmfExporter` does not do this yet.
8. **Presets are `DevicePreset` templates without ids**, so loading always creates new identities; presets exclude connections (no hardware references).
9. Old `TrackRoute.Transpose` migrates to a **Transpose device** in the track's chain; `Voice` moves onto the connection; `Channel` becomes `ChannelMapping.Force`; one `ExternalInstrument` per distinct (endpoint, profile) pair, including an "Unassigned" one for routes that have settings but no endpoint (the old code kept those).
10. `TrackRendering.Render(track, ppqn, channelOverride)`: the override is the **unique Force channel** of the track's outgoing event connections, else none. This preserves the old "two lanes collide after the channel override" behaviour.
11. **Mute and solo apply to a track's own content** (the old rule: solo overrides mute). Events routed *into* a muted track from a playing one still pass through its chain. Revisit only if the UI needs "mute silences the track's output".
12. **Device automation is not signal**: it is rendered for every track, playing or muted, applied to built-in processors at its ticks, and returned as `ParameterFeed`s for every other device. A lane whose device is missing is reported (`AutomationTargetMissing`) and kept.
13. **At compile time, a plugin MIDI effect or an unknown device passes events through, with a diagnostic.** Its real output exists only at runtime in a worker. An instrument without an in-process processor takes its `Handles` classes out of the chain (this matches `SignalPresence.Through`); SysEx and other unhandled classes go on to the track's connections.
14. **The evaluator never fails on bad routing.** It leaves out every connection named in a validator error, reports it (`InvalidConnection`), and as a backstop drops any connection that still closes a loop during topological ordering (`Feedback`). Audio connections are validated and reported but not followed (no audio engine).
15. **"Not routed"** is reported for a playing track with no outgoing event connection (from the track or a tap on its devices) and no instrument in its chain, whatever its content, like the old compiler. With an instrument but no connection, it is reported only when events remain after the chain (for example SysEx the instrument does not handle). A tap on a chain whose owning track is gone is left out and reported (it can never run).
16. **Arpeggiator phrases are anchored at the first note** played while nothing is held, including after a gap shorter than a step; then they step every rate interval while anything is held, so every phrase sounds (a legato note, starting exactly where the last one ends, continues the phrase). Held pitches are deduplicated and played lowest first; generated notes take the source note's channel, velocities and ordering key, and an `EventId` derived from the source note's ID and the step's tick. Output, IDs included, is identical across evaluations and whether processed in one block or many (tested).

## 5. Regression net (hold the refactor to these)

Frozen from the **old** pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with every endpoint present except `gone-*` (missing) and `old-*` (present as `new-*` with the same name, so bound by name), profiles from `/profiles`:

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`: Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override. `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must stay green unchanged. The fixtures live in `src/Cadence.Tests.Unit/Infrastructure/Fixtures/` and must never be regenerated.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`; initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 6. Plugin hosting (background agent)

**Update (Cadence.Signal step):** this clone (macOS, `/Users/dan/Git/Personal/Cadence`) has **no** plugin worktree: `git worktree list` shows only the main checkout, and no `worktree-agent-*` branch is on the remote. Treat the plugin work as not started and build it from the spec ("worktree is gone" path below).

A general-purpose agent was started in an isolated worktree at `.claude/worktrees/agent-*` (branch `worktree-agent-*`, based on `432e4d9`, before the plan commit) to build `Cadence.Plugins.Protocol`, `Cadence.Plugins`, `Cadence.PluginWorker`, their tests, and `docs/plugin-hosting.md`. Its spec is **`docs/daw-epic-plugin-hosting-spec.md`**.

State when last checked: about 1,100 lines of the control-plane protocol written as **untracked files** (no commits), no data-plane exchange, host, worker, scanner, tests, or docs, and it had not been built. It may still be running or may have finished by the time you read this: run `git worktree list` and `git -C <worktree> log` / `status`.

- If it finished: review its diff yourself (do not trust its report), commit and merge its branch, add the projects to `Cadence.slnx` and `DependencyDirectionTests`, build, run every suite repeatedly (flakiness), then write the Application bridge.
- If the worktree is gone or stale: build it from the spec, in this order: protocol, exchange, worker, host, scanner, integration tests. Real worker processes must be spawned and `Process.Kill()`ed in tests.
- Either way, **the epic requires real process-kill tests** (prompt section 22). Mocks are not enough.

## 7. Remaining work, in order

1. ~~`Cadence.Signal`~~: **done** (section 4).
2. **Replace routing** (one coherent change; the old types go away): remove `TrackRoute`, `RoutingTable`, `ResolvedRoute`, `PlanTrackBinding`; rework `PlaybackPlanCompiler` to compile processed streams (carry origin track index + index for ordering), `RouteResolver` to resolve instrument ports, `PlaybackRouting`, `PlaybackController` (`Routes`, `ChannelFor`, `SoundOn` / audition / thru must follow the track's chain and connections), `ProjectCommands` (`SetRoute`, `RemoveTrack`, `DuplicateTracks` copy routes today), `ProjectSerializer`, `SmfExporter` (report device lanes), and `PlanDump` plus tests that used `PlanTrackBinding` (about 15 files use `TrackRoute`). Keep `ProfileReference`, `EndpointReference`, `VoiceAssignment`. Then add the format-3 equivalence test asserting the section 5 hashes through the **new** pipeline. How to use `Cadence.Signal` for this: `Cadence.Playback` (or `Cadence.Application`) takes a reference to `Cadence.Signal` (update `DependencyDirectionTests`). The compiler takes `SignalGraphResult.ExternalParts`: each part's port resolves to an endpoint and an output slot, its `Voice` resolves through the instrument's profile into initial events (negative sequence, origin = the source track's index; for a rack source, decide and document), and events are encoded and sorted by `(tick, phase, Origin, Sequence)`. Split-SysEx joining must stay **per source track**: group raw events by `Origin` before joining. "Not routed" and the other diagnostics come from `SignalGraphResult.Diagnostics`; map `Track` to `PlanDiagnostic`. A routing command that would create an error must be refused with a readable message (commands are pure; throw a dedicated exception and have `MainViewModel.Execute` surface it).
3. **Format 4** (`ProjectSerializer.CurrentFormatVersion = 4`, `Format3To4Migration : IProjectMigration`): write `role`, `group`, clip type `"audio"`, `instruments`, `chains` (devices with parameters, bypass, state as base64), `connections`, `mixer`, device automation targets. Migration per decision 9; tracks get `role: "instrument"`. Preserve unknown properties; old `routing` data is converted, never dropped. Call `TrackRoleConversion.Reconcile` on load and report changes. Chain preset file format (`cadence-chain-preset`, v1) + serializer.
4. **Application**: commands for add/remove/move/bypass device, set parameter, load/save chain preset (new identities), move chain (keeps identity), add/remove/edit connection (validated), create instrument/port, mixer channel edits, role change and clip-add using `RoleChangePlan`, `SetGroup`. Bridge to plugin hosting: `DeviceInstance` to plugin instance, capture state into the project, status events.
5. **Presentation + Desktop**: track role, device strip view models (insert, remove, reorder, bypass, status such as `[Vital: Crashed]`, routing indicators), routing inspector bound to `Connections`, and keep the existing inspector fields (Output/Profile/Channel/Transpose/Voice) working through the new model (Output picks or creates an `ExternalInstrument`; Transpose edits a Transpose device). View-model tests; build `Cadence.Desktop`. State plainly that the AXAML was never run. `TimelineView` and `ArrangementViewModel` must draw clips that are not `NoteClip`.
6. **Tests for all 15 acceptance scenarios** (prompt section 19) and the testing list (section 22). Existing domain tests already cover scenarios 1 (structure), 4, 5 (targets and rendering), 7, 8 (structure), 9, 10, 14 at the model level; the processing, playback, persistence, and plugin halves remain.
7. **Docs**: rewrite `docs/architecture.md`; update `docs/project-format.md`; ADRs 0021 to 0027 (titles in the plan) with explicit supersession: ADR 0008 (routing) superseded; ADR 0020 amended ("no track kind" and "automation targets are MIDI only" no longer hold); update `docs/adr/README.md`; a native technology policy ADR (C# first, Rust only when measured, C/C++ only for third-party/vendor/ABI shims, versioned C ABI rules); README status and layout, clearly separating implemented from planned; list deferred features. Update `.github/workflows/ci.yml` only if the worker needs it.
8. Final: full build with 0 warnings, all suites, format check (section 2), then the report the prompt asks for (section 26), including what was **not** verified (UI never run; plugin hosting only against reference plugins; no real VST3).

## 8. Deferred on purpose (say so in the docs)

Real VST3 loading and parameter enumeration, plugin editors, audio engine and audio clip playback, plugin delay compensation across a graph, MIDI 2.0 transport, visual node graph, feedback routing, OS-level sandboxing, mixer parameter automation, nested group UI. Process isolation is crash isolation only, not a security boundary; say so.

## 9. Open risks

- Replacing routing touches many files; do it in one pass and lean on the section 5 hashes.
- `PlaybackEngine` and `ChaseState` hold encoded MIDI 1.0 messages; only the compiler's input changes. Keep that boundary.
- Audition, thru, and recording use the track's resolved output; they must keep working through connections.
- The project file cannot yet store any new-model entity. Do not ship a UI that creates them before format 4 exists.
