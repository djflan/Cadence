# DAW architecture epic: handoff

Written mid-work so another agent can continue. Delete this file when the epic is done.
Read `docs/daw-architecture-plan.md` first (assessment, decisions, phases). This file is what is
**left**, plus the traps already found.

Branch: `claude/busy-gates-qengpq`. Do not open a PR unless asked.

## 1. Honest status

| Area | State |
| ---- | ----- |
| Repo assessment, plan | Done, committed (`docs/daw-architecture-plan.md`). |
| Format 3 fixtures + pre-refactor hashes | Done, committed. See section 6. |
| Domain model (`Cadence.Domain`) | Written. **`Cadence.Domain` builds with 0 warnings.** |
| Domain tests (`Cadence.Tests.Unit/Domain/...`) | Written but **never compiled or run**. Two have known-bad assertions (section 3). |
| Whole solution | **Not rebuilt since the domain changes.** Changes were additive, but unverified. |
| `Cadence.Signal` project | Not started. |
| Replacing `TrackRoute`/`RoutingTable` | Not started. `Project.Routing` still exists on purpose. |
| Persistence format 4 + migration | Not started. |
| Application commands, Presentation, Desktop UI | Not started. |
| Plugin hosting | Delegated to a background agent in a git worktree. **Unverified, unmerged. See section 7.** |
| ADRs, architecture docs, README, CI | Not started. |
| Rust | Deliberately not introduced. Nothing measured needs it. |

Nothing here is a finished capability yet. Do not describe any of it as complete in docs.

## 2. Build environment (non-obvious, costs time if missed)

- `dotnet` was installed from Ubuntu apt: SDK **10.0.112**. `global.json` pins **10.0.300**, so any
  `dotnet` run from inside the repo fails ("compatible SDK not found").
- **Run every dotnet command from `/tmp` with absolute paths**, and never edit `global.json`:
  `cd /tmp && dotnet build /home/user/Cadence/src/Cadence.slnx --no-restore`
  (restore once: `dotnet restore /home/user/Cadence/src/Cadence.slnx`).
- `global.json` also selects the Microsoft.Testing.Platform runner, which does not apply from `/tmp`.
  **Run the test executables directly:**
  `/home/user/Cadence/src/Cadence.Tests.Unit/bin/Debug/net10.0/Cadence.Tests.Unit`
  (xUnit v3; filters: `-method "Ns.Class.Method"`, `-class`, `-namespace`). Same for `Cadence.Tests.Integration`.
- Format check: `cd /tmp && dotnet format /home/user/Cadence/src/Cadence.slnx --verify-no-changes`.
- If apt is needed again: run it in the foreground. A backgrounded install looked dead but was running.
- Outbound: nuget.org, archive.ubuntu.com, pypi work. `dot.net` and `builds.dotnet.microsoft.com` return 403.
- Linux, headless: the Avalonia UI cannot be launched here. The `Cadence.Desktop` project can only be
  built, and its compiled bindings are checked at build time.
- **Never `git add -A` blindly.** `.claude/worktrees/` holds the plugin agent's worktree; it is excluded
  in `.git/info/exclude` locally, but a fresh clone will not have that. Add paths explicitly.

Baseline before any change: build clean, **691 unit tests** pass, **44 integration tests** (28 pass, 16 are
CoreMIDI/WinMM tests skipped on Linux).

## 3. Fix first (the last edit was rejected, so these were never applied)

1. `src/Cadence.Tests.Unit/Domain/Sequencing/TrackRoleTests.cs`
   - In `Reconcile_WidensTracksWhoseRoleCannotHoldTheirClips_AndNeverNarrows`, the last assertion is
     nonsense (`Assert.Equal((project, 0), ...)`). Replace it with:
     `var (again, none) = TrackRoleConversion.Reconcile(result); Assert.Same(result, again); Assert.Empty(none);`
   - In `EffectAndGroupTracks_RefuseClips_...`, `Assert.Same(project, project);` is vacuous. Replace with
     `Assert.Empty(project.Sequence.FindTrack(track.Id)!.Clips);` and assert the message contains "do not hold clips".
2. **Write two test files that were never created:**
   - `Domain/Sequencing/DeviceAutomationTests.cs`: a device target is not MIDI, `CreateEvent` throws,
     `ForDevice(default, ...)` throws, equality by device+parameter, two lanes on one device parameter are
     rejected, `TrackRendering.Render` puts device lanes in `ParameterChanges` and never in `Events`
     (MIDI FX + synth + filter lanes plus a CC lane), linear device lanes sample at full resolution,
     reordering devices changes no target (`chain.Move` keeps `DeviceId`), and `Track.FirstChannel` ignores device lanes.
   - `Domain/Routing/ChannelMappingTests.cs`: preserve, force, remap, `Only` filter runs before mapping,
     duplicate remap rejected, force+remap inconsistent, content equality.
3. Build the whole solution, run all unit tests, fix whatever the analyzers say, then commit.

Keep assertions plain. Two bad tests in a row came from over-clever expressions.

## 4. Analyzer traps already hit (warnings are errors)

- `CA2208`: `new ArgumentException(msg, nameof(X))` must name a *parameter of the enclosing method*.
  Inside a static helper use that helper's parameter name; `nameof(Property)` is only allowed inside the property's own accessor.
- `CA1711`: an enum member cannot end in `Ex` (`SysEx` became `SystemExclusive`).
- `ImmutableArray<T>` has no `FindIndex`; use a loop.
- Positional records that re-declare their properties are awkward; use an explicit constructor (as `ProfileReference` does).
- Style: file-scoped namespaces, braces always, `_camelCase` fields, XML docs only where intent/units/ownership are not obvious, tests named `Subject_Condition_Expectation`.
- `Cadence.Domain` may reference only the BCL (`DependencyDirectionTests`). Add new projects to that test.

## 5. What exists, and the decisions behind it (do not re-decide these)

### Committed
- `docs/daw-architecture-plan.md`; `Fixtures/format3-routing-full.cadence`, `format3-canon.cadence`.

### Uncommitted (on disk), `src/Cadence.Domain`
- `Devices/`: `DeviceId`, `DeviceChainId`, `ParameterId(uint)`, `DeviceDefinitionId`, `DeviceReference`,
  `DeviceDefinition` (declarative: `Consumes`, `Produces`, `Handles`, parameters), `ParameterDescriptor`,
  `DeviceInstance` (stable id, parameters, bypass, `PluginState`), `DeviceChain` (+ `ChainOwner`: Track or Rack),
  `DevicePreset`/`DeviceChainPreset` (templates with **no ids**), `SignalKinds`, `DeviceDefinitionLookup`.
- `Sequencing/`: `TrackRole`, `AudioClip` + `AudioSource` (metadata only, no audio engine), `EventClass`
  (flags: Notes, Controllers, Programs, SystemExclusive, Meta). `Track` gained `Role` and `Group` (all copy paths carry them).
  `AutomationTarget` gained a device-parameter variant (`ForDevice`, `IsMidi`). `TrackRendering` now returns
  `RenderedTrack.ParameterChanges` separate from MIDI events. `ControlValue.FromFraction/ToFraction` added.
- `Routing/`: `ExternalInstrument` (+ `ExternalPort`, ports carry endpoints), `ChannelMapping`
  (Only, Force, Remap; order: filter then map), `SignalNode`, `SignalConnection` (kind, source, destination, mapping, voice),
  `SignalPresence`/`SignalFlow`, `SignalRoutingValidator` (existence, kind fit, "nothing to carry", cycles, duplicates, automation warnings).
- `Mixing/Mixer.cs`: `MixerChannel`, `Mixer` (independent of tracks).
- `Projects/Project.cs`: new `Instruments`, `Chains`, `Connections`, `Mixer` with validation (one chain per track, device ids unique across chains) and lookups.
- `Projects/TrackRoleConversion.cs`: plans (`Automatic` / `NeedsConfirmation` / `Refused`) for adding clips or changing role; `Reconcile` only widens.

### Decisions
1. **Role is persisted and reconciled** with content (cannot be derived for empty/Effect/Group tracks). `Track` never rejects clips because of role. Effect/Group refuse clips; narrowing that would hide content is refused; conversion never discards.
2. **One routing model**: `SignalConnection` list. Sources: track output, rack output, device tap. Destinations: track/rack input, external-instrument part (port + channel via mapping), mixer channel, master. The strip and inspector must both edit this list only.
3. **Chains are first-class**, one owner each, addressed by `DeviceChainId`; a track has at most one chain (optional, so `with { Sequence = ... }` keeps working).
4. **External instruments are project entities**; profile stated once per instrument. There is **no** "external instrument device kind": that would be a second way to express a route.
5. **Passthrough** is by `EventClass`: a device is given only the classes in `Handles`; everything else is merged back in canonical order. Only explicit filter devices drop events (SysEx/XG data must survive note processors).
6. **Processing runs at plan-compile time** for built-in processors (immutable plans stay; the real-time thread only dispatches). Plugins process in workers at runtime. Software-instrument event streams are produced but **not played** (no audio engine): emit a diagnostic, never imply support.
7. **Device automation is never MIDI**: it becomes `ParameterChange`s. SMF export must report (not silently drop) device lanes.
8. **Presets are `DevicePreset` templates without ids**, so loading always creates new identities; presets exclude connections (no hardware refs).
9. Old `TrackRoute.Transpose` migrates to a **Transpose device** in the track's chain; `Voice` moves onto the connection; `Channel` becomes `ChannelMapping.Force`; one `ExternalInstrument` per distinct (endpoint, profile) pair, including an "Unassigned" one for routes with settings but no endpoint (the old code kept those).
10. `TrackRendering.Render(track, ppqn, channelOverride)`: the override is the **unique Force channel** of the track's outgoing event connections, else none. This preserves the old "two lanes collide after the channel override" behaviour.

## 6. Regression net (hold the refactor to these)

Frozen from the **old** pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with all endpoints present
except `gone-*` (missing) and `old-*` (present as `new-*`, same name, so bound by name), profiles from `/profiles`:

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`:
Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override.
`MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must also stay green unchanged.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`;
initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 7. Plugin hosting (background agent)

A general-purpose agent was started in an isolated worktree at `.claude/worktrees/agent-*` (branch `worktree-agent-*`,
based on `432e4d9`, before the plan commit) to build `Cadence.Plugins.Protocol`, `Cadence.Plugins`,
`Cadence.PluginWorker`, their tests, and `docs/plugin-hosting.md`. At last check it had created project folders
and **no commits**. Its spec is the long prompt in the session; the requirements it was given:
- worker always out of process, **no silent in-process fallback**; `InProcessTrusted` must throw "deferred";
- control plane over named pipes; data plane = file-backed shared-memory block exchange (pipelined, non-blocking host calls, faulted flag, generation numbers, late results discarded);
- supervisor: crash and hang detection, restart policy with back-off and a quarantine guard, state snapshot and restore, silence on failure (never stale audio);
- scanner in a separate process per module, cache + quarantine, failures reported;
- reference plugins inside the worker (gain, sine, transpose-with-passthrough, failure hooks). **No VST3 loading and no VST3 claims.**
- tests spawn and `Process.Kill()` real workers; run the integration tests repeatedly to find flakiness.

**If this session ends the worktree may be lost.** Check `git worktree list` and `git -C <worktree> log`. If it finished,
review its diff yourself (do not trust its report), merge its branch, build everything, run all suites, then write the bridge.
If it is gone, the work is to be redone from the requirements above, in that order: protocol, exchange, worker, host, scanner, integration tests.

## 8. Remaining work, in order

1. Section 3 fixes; build solution; run unit tests; commit.
2. **`Cadence.Signal`** (new project, depends on Domain only; add to slnx and `DependencyDirectionTests`):
   `SignalBuffer` (reusable, ping-pong, no per-stage allocation for passthrough), `ISignalProcessor`,
   `ChainRunner` (hands a device only its `Handles` classes, merges the rest back in canonical order, bypass = transparent),
   `DeviceCatalog` (definitions + processor factories; plugin definitions are data only),
   built-ins `Transpose` (drops out-of-range notes and reports them), `EventFilter`, `Arpeggiator`
   (3 notes become 12), and a graph evaluator that turns tracks + chains + connections into destination streams
   (external parts, software-instrument streams, parameter changes, diagnostics). Tests for Scenarios 2, 3, 4, 6, 10; zero-allocation passthrough test.
3. **Replace routing** (one coherent change; the old types go away): remove `TrackRoute`, `RoutingTable`, `ResolvedRoute`,
   `PlanTrackBinding`; rework `PlaybackPlanCompiler` to compile processed streams (carry origin track index + index for ordering),
   `RouteResolver` to resolve instrument ports, `PlaybackRouting`, `PlaybackController` (`Routes`, `ChannelFor`, `SoundOn`/audition/thru must follow the track's chain and connections),
   `ProjectCommands` (`SetRoute`, `RemoveTrack`, `DuplicateTracks` currently copy routes), `ProjectSerializer`, `SmfExporter` (report device lanes),
   `PlanDump`/tests that used `PlanTrackBinding` (about 15 files use `TrackRoute`). Keep `ProfileReference`, `EndpointReference`, `VoiceAssignment`.
   Then add the format-3 equivalence test asserting section 6 hashes through the **new** pipeline. A routing command that would create an error must be refused with a readable message (commands are pure; throw a dedicated exception and have `MainViewModel.Execute` surface it).
4. **Format 4** (`ProjectSerializer.CurrentFormatVersion = 4`, `Format3To4Migration : IProjectMigration`): write `role`, `group`, clip type `"audio"`,
   `instruments`, `chains` (devices with parameters, bypass, state as base64), `connections`, `mixer`, device automation targets. Migration per decision 9;
   tracks get `role: "instrument"`. Preserve unknown properties and old `routing` data is converted, never dropped. Add `Reconcile` on load and report changes. Chain preset file format (`cadence-chain-preset`, v1) + serializer.
5. **Application**: commands for add/remove/move/bypass device, set parameter, load/save chain preset (new identities), move chain (keeps identity),
   add/remove/edit connection (validated), create instrument/port, mixer channel edits, role change and clip-add with `RoleChangePlan`, `SetGroup`.
   Bridge to plugin hosting: `DeviceInstance` to plugin instance, capture state into the project, status events.
6. **Presentation + Desktop**: track role, device strip view models (insert, remove, reorder, bypass, status such as `[Vital: Crashed]`, routing indicators),
   routing inspector bound to `Connections`, and keep the existing inspector fields (Output/Profile/Channel/Transpose/Voice) working through the new model
   (Output picks or creates an `ExternalInstrument`; Transpose edits a Transpose device). View-model tests; build `Cadence.Desktop`. State plainly that the AXAML was not run.
7. **Tests for all 15 acceptance scenarios** (epic section 19) and the testing list (section 22).
8. **Docs**: rewrite `docs/architecture.md`; update `docs/project-format.md`; ADRs 0021 to 0027 (titles listed in the plan) with
   explicit supersession: ADR 0008 (routing) superseded; ADR 0020 amended ("no track kind" and "automation targets are MIDI only" no longer hold; update `docs/adr/README.md`);
   native technology policy ADR (C# first, Rust only when measured, C/C++ only for third-party/vendor/ABI shims, versioned C ABI rules);
   README status and layout, clearly separating implemented from planned; list deferred features. Update `.github/workflows/ci.yml` only if the worker needs it.
9. Final: full build with 0 warnings, all suites, `dotnet format --verify-no-changes`, then the report the epic asks for (section 26), including what was **not** verified (UI never run; plugin hosting only against reference plugins; no real VST3).

## 9. Deferred on purpose (say so in docs)

Real VST3 loading and parameter enumeration, plugin editors, audio engine and audio clip playback, plugin delay compensation across a graph,
MIDI 2.0 transport, visual node graph, feedback routing, OS-level sandboxing, mixer parameter automation, nested group UI.
Process isolation is crash isolation only, not a security boundary; say so.

## 10. Open risks

- The domain/test code has never been compiled together; expect a round of fixes.
- Replacing routing touches many files; do it in one pass and lean on the section 6 hashes.
- `PlaybackEngine` and `ChaseState` hold encoded MIDI 1.0 messages; only the compiler's input changes, keep that boundary.
- `TimelineView` and `ArrangementViewModel` must still draw clips that are not `NoteClip` (audio clips) without crashing.
- Audition, thru, and recording use the track's resolved output; they must keep working through connections.
