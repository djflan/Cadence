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
| Domain tests | Done. **Whole solution builds with 0 warnings; 768 unit tests pass** (691 baseline + 77 new); integration 44 total, 0 failed, 16 platform-skipped (same as baseline); `dotnet format --verify-no-changes` passes (run on a scratch copy, see section 2). |
| `Cadence.Signal` project | **Not started.** Next. |
| Replacing `TrackRoute` / `RoutingTable` | Not started. `Project.Routing` still exists on purpose. Nothing uses the new `Chains`, `Connections`, `Instruments`, or `Mixer` yet. |
| Persistence format 4 + migration | Not started. The serializer does not write or read any new type (roles, audio clips, devices, connections, device automation lanes would currently throw or be lost on save). |
| Application commands, Presentation, Desktop UI | Not started. |
| Plugin hosting | **Incomplete, uncommitted, unmerged, never compiled by me.** See section 6. |
| ADRs, architecture docs, README, CI | Not started (only the plan and this handoff exist). |
| Rust | Deliberately not introduced. Nothing measured needs it. |

Nothing here is a finished capability. Do not describe any of it as complete in the docs. In particular, saving a project that contains a new-model entity is **not** supported yet.

## 2. Build environment (non-obvious, costs time if missed)

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

## 5. Regression net (hold the refactor to these)

Frozen from the **old** pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with every endpoint present except `gone-*` (missing) and `old-*` (present as `new-*` with the same name, so bound by name), profiles from `/profiles`:

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`: Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override. `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must stay green unchanged. The fixtures live in `src/Cadence.Tests.Unit/Infrastructure/Fixtures/` and must never be regenerated.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`; initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 6. Plugin hosting (background agent)

A general-purpose agent was started in an isolated worktree at `.claude/worktrees/agent-*` (branch `worktree-agent-*`, based on `432e4d9`, before the plan commit) to build `Cadence.Plugins.Protocol`, `Cadence.Plugins`, `Cadence.PluginWorker`, their tests, and `docs/plugin-hosting.md`. Its spec is **`docs/daw-epic-plugin-hosting-spec.md`**.

State when last checked: about 1,100 lines of the control-plane protocol written as **untracked files** (no commits), no data-plane exchange, host, worker, scanner, tests, or docs, and it had not been built. It may still be running or may have finished by the time you read this: run `git worktree list` and `git -C <worktree> log` / `status`.

- If it finished: review its diff yourself (do not trust its report), commit and merge its branch, add the projects to `Cadence.slnx` and `DependencyDirectionTests`, build, run every suite repeatedly (flakiness), then write the Application bridge.
- If the worktree is gone or stale: build it from the spec, in this order: protocol, exchange, worker, host, scanner, integration tests. Real worker processes must be spawned and `Process.Kill()`ed in tests.
- Either way, **the epic requires real process-kill tests** (prompt section 22). Mocks are not enough.

## 7. Remaining work, in order

1. **`Cadence.Signal`** (new project, depends on Domain only; add to slnx and `DependencyDirectionTests`): `SignalBuffer` (reusable, ping-pong, no per-stage allocation for passthrough), `ISignalProcessor`, `ChainRunner` (gives a device only its `Handles` classes, merges the rest back in canonical order, bypass is transparent), `DeviceCatalog` (definitions + processor factories; plugin definitions are data only), built-ins `Transpose` (drops out-of-range notes and reports them), `EventFilter`, `Arpeggiator` (3 notes become 12), and a graph evaluator that turns tracks + chains + connections into destination streams (external parts, software-instrument streams, parameter changes, diagnostics). Tests for scenarios 2, 3, 4, 6, 10 and a zero-allocation passthrough test.
2. **Replace routing** (one coherent change; the old types go away): remove `TrackRoute`, `RoutingTable`, `ResolvedRoute`, `PlanTrackBinding`; rework `PlaybackPlanCompiler` to compile processed streams (carry origin track index + index for ordering), `RouteResolver` to resolve instrument ports, `PlaybackRouting`, `PlaybackController` (`Routes`, `ChannelFor`, `SoundOn` / audition / thru must follow the track's chain and connections), `ProjectCommands` (`SetRoute`, `RemoveTrack`, `DuplicateTracks` copy routes today), `ProjectSerializer`, `SmfExporter` (report device lanes), and `PlanDump` plus tests that used `PlanTrackBinding` (about 15 files use `TrackRoute`). Keep `ProfileReference`, `EndpointReference`, `VoiceAssignment`. Then add the format-3 equivalence test asserting the section 5 hashes through the **new** pipeline. A routing command that would create an error must be refused with a readable message (commands are pure; throw a dedicated exception and have `MainViewModel.Execute` surface it).
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
