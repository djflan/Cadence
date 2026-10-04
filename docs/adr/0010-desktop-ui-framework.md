# 0010. Desktop UI framework: Avalonia

- Status: Accepted
- Date: 2026-10-04

## Context

The desktop UI must:

1. run on Windows, macOS, and Linux;
2. be written in .NET;
3. render fast, ideally on the GPU, including dense custom views (timeline, piano roll, automation);
4. look like a polished commercial instrument, not a stock-widget engineering tool;
5. scale to different window sizes, densities, and font sizes.

It must also support accessibility (screen readers, keyboard-first use, high contrast, no colour-only
status), stay a thin client of the application layer, and be testable.

## Options

| | Avalonia 12 | Uno Platform 6 | .NET MAUI 10 | Blazor Hybrid / Photino | Eto.Forms |
|---|---|---|---|---|---|
| Windows / macOS / Linux | Yes / Yes / Yes | Yes / Yes / Yes | Yes / Yes (Catalyst) / **No** | Yes / Yes / Yes (WebKitGTK) | Yes / Yes / Yes |
| Rendering | Own Skia renderer, GPU backends | Skia renderer (desktop) | Native controls | Embedded web view | Native controls |
| Bespoke visual design | Full control templating and styles, identical on every OS | Full (WinUI XAML styling) | Limited by native controls | Full (CSS) | Native look only |
| Custom high-performance drawing | Direct `DrawingContext` controls | Skia canvas / composition | Platform-specific | Canvas/WebGL via JS interop | Limited |
| Accessibility | UI Automation (Windows), NSAccessibility (macOS); Linux AT-SPI less mature | Platform bridges | Native | Browser accessibility tree | Native |
| Interop with managed engine | In-process, no bridge | In-process | In-process | JS/.NET bridge for UI updates | In-process |
| Testing | Headless platform; view models testable without UI | Runtime tests | Device tests | Web/bUnit | Limited |
| Health / licence | Very active, MIT | Active, Apache-2.0 | Active, MIT | Active, MIT | Smaller community, BSD |

MAUI fails the Linux requirement. Eto.Forms cannot deliver a distinctive design. Blazor Hybrid could
look excellent, but splits the UI into web technology, adds a bridge on every playhead update, and
pushes dense editors into JavaScript canvas code. Uno is a credible alternative with a heavier
toolchain and a WinUI-shaped API surface.

## Decision

Use **Avalonia 12** with **CommunityToolkit.Mvvm**, plus a Cadence design system: a custom dark
theme layered on Fluent, with Cadence-owned resources (colour, type, spacing, radii) and custom
controls for the timeline. The maintainer's independent research also preferred Avalonia.

- View models are plain classes over `ProjectSession` and `PlaybackController`, with no Avalonia
  types, and are unit tested. The UI never talks to MIDI or files directly.
- Display updates (playhead, meters) poll the engine on a UI timer at about 30 Hz. UI timers never
  schedule MIDI.
- The GPU rendering backend is requested explicitly per platform (Metal first on macOS).

## Consequences

- Linux screen-reader support must be verified on target distributions before Cadence claims it.
- `Avalonia.Headless.XUnit` 12.1 is built against xUnit v3 3.x and fails test discovery on xUnit v3
  4.x. Headless UI tests are deferred until it catches up; view-model tests cover behavior meanwhile.
- A custom theme means Cadence owns focus visuals, contrast, and states for every control it
  restyles; these are part of review.
