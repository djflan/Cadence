# Cadence

[![CI](https://github.com/djflan/Cadence/actions/workflows/ci.yml/badge.svg)](https://github.com/djflan/Cadence/actions/workflows/ci.yml)

Cadence is a modern, open-source, cross-platform MIDI workstation built for musicians who use real instruments.

It is initially focused on excellent Yamaha XG and QY100 workflows, while its architecture is deliberately broader: Cadence can describe what an instrument understands independently from where MIDI is sent. A track can therefore use a device profile with physical hardware, a virtual MIDI port, a network destination, or a compatible software instrument.

> [!NOTE]
> Cadence is an independent open-source project and is not affiliated with, authorized, sponsored, or endorsed by Yamaha Corporation. Yamaha, XG, QY100, and other product names and trademarks belong to their respective owners and are used solely to describe compatibility.

![Cadence main window](docs/images/cadence-main-window.png)

## Status

or WinMM on Windows with measured timing, or the ALSA sequencer on Linux (not yet tested on Linux), show what was sent in a built-in MIDI monitor, and save and reopen projects safely. Windows MIDI Services and note editing are next.

### Platform support

| Platform | App | MIDI output | Notes |
| -------- | --- | ----------- | ----- |
| macOS (Apple Silicon) | Yes | CoreMIDI: hardware, IAC, network, and Cadence's own virtual port | Development platform; native menu bar; real-time playback thread |
| Windows (ARM64 tested) | Yes | WinMM: hardware ports and software synths, including SysEx | Verified with a VST synth through a Windows MIDI Services loopback; see [docs/windows-vst-loopback.md](docs/windows-vst-loopback.md) |
| Linux | Builds; not yet verified | ALSA sequencer (direct sends, SysEx, *Cadence Out* virtual port); untested on Linux | No ALSA input or hot-plug yet; screen-reader support unverified |

## Goals

- Hardware-first MIDI sequencing, recording, editing, and playback
- Reliable timing with measurable scheduling behavior
- First-class, data-defined device profiles for voices, banks, controllers, effects, SysEx, and capabilities
- Independent MIDI endpoints for physical, virtual, network, and future hosted-software destinations
- A portable project format that survives missing or renamed devices
- Standard MIDI File import and export with honest reporting of lossy conversions
- Accessible cross-platform desktop workflows
- A clean extension path for GM, GM2, XG, GS, and community-defined instruments

## Architectural principle

Cadence separates a **device profile** from a **MIDI endpoint**:

```text
Track / logical part
        |
        +-- Device profile: what the instrument understands
        |      voices, banks, controllers, SysEx, capabilities
        |
        +-- MIDI endpoint: where messages are sent
               physical port, virtual port, network, software instrument
```

That makes all of these legitimate configurations:

```text
QY100 profile       -> physical QY100
QY100 profile       -> compatible XG software synth
Generic XG profile  -> physical XG module
GM/GM2 profile      -> arbitrary compliant endpoint
Custom profile      -> virtual MIDI port
```

Profiles are not ports, and ports are not instruments. Projects retain their musical intent even when a previously selected endpoint is unavailable.

## Technology direction

Cadence uses .NET and C# for its domain, application, persistence, profile, and cross-platform UI code. Platform adapters isolate operating-system MIDI services.

C or C++ components may be introduced where native MIDI APIs or measured high-resolution scheduling requirements justify them. Any native component must remain behind a narrow, versioned C ABI; vendor knowledge and domain rules stay in managed code.

## Repository layout

```text
Cadence/
├── CONTRIBUTING.md
├── LICENSE
├── README.md
├── global.json
├── docs/
│   ├── adr/            Architecture decision records
│   ├── midi-files.md   Standard MIDI File behavior and diagnostics
│   ├── profiles.md     Device profile format
│   └── project-format.md  Cadence project files, saving, and recovery
├── profiles/           Shipped device profiles (data, not code)
├── samples/            Redistributable demo and fixture files
└── src/
    ├── Cadence.slnx
    ├── SubModules/
    └── <ProjectFolder>/
```

- `src/Cadence.slnx` is the solution entry point.
- `src/SubModules/` is reserved for shared source submodules.
- Each normal project belongs in its own direct child folder under `src/`.
- `docs/adr/` records consequential architecture decisions.
- `profiles/` holds device profile data; see `profiles/README.md` for contribution rules.

The repository is intentionally minimal while the first vertical slice is designed. Empty architectural layers should not be generated simply to match a diagram.

## Building

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (10.0.300 or a later 10.0 feature band; see `global.json`). Any editor works; in Visual Studio, use a version that supports .NET 10 and opens `src/Cadence.slnx`. From the repository root run:

```sh
dotnet restore src/Cadence.slnx
dotnet build src/Cadence.slnx
dotnet test src/Cadence.slnx
dotnet format src/Cadence.slnx --verify-no-changes
```

Benchmarks and a real-time jitter probe live in `src/Cadence.Benchmarks`:

```sh
dotnet run -c Release --project src/Cadence.Benchmarks -- --filter '*' --job Short
dotnet run -c Release --project src/Cadence.Benchmarks -- --jitter 10 --realtime
```

Warnings are treated as errors and code style is enforced during build. Tests run on Microsoft.Testing.Platform; the default suite needs no MIDI hardware. Platform MIDI adapters may require their target operating system.

## Running Cadence

```sh
dotnet run --project src/Cadence.Desktop
```

Without any hardware, route tracks to **Cadence Monitor** and watch the messages in the MIDI
monitor panel, or route to **Cadence Out** and select it as the input of a software synth or DAW.
A demo song is included in `samples/cadence-demo.mid`; drag it (or any `.mid` or `.cadence` file)
onto the window to open it. Hardware checks are listed in
[docs/hardware-test-plan.md](docs/hardware-test-plan.md).

| Action | Shortcut |
| ------ | -------- |
| Play / pause | Space |
| Return to start / go to end | Home / End |
| Previous / next bar | , / . |
| Loop four bars from the playhead | L |
| Panic (silence every output) | Ctrl/Cmd + . |
| Select tracks | Click; Shift-click for a range; Ctrl/Cmd-click to add or remove; ↑ / ↓ (Shift to extend) |
| Select all tracks | Ctrl/Cmd + A |
| Rename / mute / solo / delete selected tracks | Return / M / S / Delete |
| Add / duplicate tracks | Ctrl/Cmd + T / Ctrl/Cmd + D |
| Undo / Redo | Ctrl/Cmd + Z / Ctrl/Cmd + Shift + Z |
| New, Open, Save, Save As | Ctrl/Cmd + N, O, S, Shift + S |
| Import / Export MIDI | Ctrl/Cmd + I / E |
| Zoom timeline | Ctrl/Cmd + scroll, trackpad pinch, scroll over the bar ruler, or Ctrl/Cmd + = / − |
| Keyboard shortcut reference | Ctrl/Cmd + / |

With several tracks selected, the inspector changes output, profile, channel, transpose, and voice
for all of them at once (one undo step); values that differ show *Mixed*.

On macOS, all commands are also in the standard menu bar (File, Edit, Track, Transport, View,
Window, Help, and *About Cadence* in the application menu).

Accessibility: every control has a screen-reader name, status is always shown with an icon and a
word as well as colour, and all transport and file actions have keyboard shortcuts. Linux
screen-reader support has not yet been verified.

## Contributing

Cadence welcomes focused contributions to sequencing, MIDI interoperability, device profiles, platform adapters, accessibility, documentation, and testing. Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request, and follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described in [SECURITY.md](SECURITY.md).

Compatibility names must be used factually and must not imply vendor affiliation, certification, sponsorship, or endorsement.

## Supporting Cadence

If Cadence is useful to you, you can support its development through [Ko-fi](https://ko-fi.com/djflan) or [GitHub Sponsors](https://github.com/sponsors/djflan).

## License

Cadence is licensed under the [MIT License](LICENSE).
