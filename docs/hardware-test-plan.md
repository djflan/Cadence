# Manual hardware test plan

The automated suite covers everything that can be checked without instruments: virtual CoreMIDI
ports, the built-in monitor, and (when enabled) the IAC bus. This checklist covers what needs a
real instrument. The first target is a Yamaha QY100 or another XG module on a USB-MIDI interface.
Record the date, macOS version, interface, and results for each run.

> Yamaha, XG, and QY100 are trademarks of Yamaha Corporation; Cadence is not affiliated with Yamaha.

## Setup

1. Connect the interface's MIDI OUT to the instrument's MIDI IN (and MIDI OUT to MIDI IN for input tests).
2. Confirm the interface appears in **Audio MIDI Setup → MIDI Studio**.
3. Start Cadence: `dotnet run --project src/Cadence.Desktop`.

## Checks

| # | Step | Expected |
| - | ---- | -------- |
| 1 | Open the inspector's **Output** list | The interface port is listed as *Hardware* with its name; Cadence Monitor and Cadence Out are also listed |
| 2 | Import `samples/cadence-demo.mid`, route all tracks to the interface (**Use this output for all tracks**) | All tracks show **Ready** |
| 3 | Press Space | The instrument plays drums, bass, keys, and lead in time; the status bar shows 0 late, 0 dropped |
| 4 | Select *Bass*, choose profile **Generic XG**, voice bank *Normal voices (MSB 0)*, any program; play from the start | The bass voice changes at the start of playback |
| 5 | Select a track with an XG profile and press **Initialize Instrument…** | A confirmation names *XG System On*; **Cancel** sends nothing; **Send** resets the instrument (check its display) |
| 6 | While playing, press Ctrl/Cmd + . (Panic) | All sound stops immediately; playback continues |
| 7 | Hold a long note (loop four bars with L, play), then press Stop | No hanging notes |
| 8 | Unplug the interface during playback | The track shows **Offline** with "not connected"; nothing crashes |
| 9 | Plug it back in | The track returns to **Ready**; playback resumes sending to it |
| 10 | Save, quit, unplug, reopen the project | The route is kept and explained as disconnected; reconnecting restores it |
| 11 | Edit while playing (mute a track, change transpose) | Changes are heard within a beat; no stuck notes |

## Timing measurement (optional)

Loop the demo for one minute and note the status bar's *p95* and *late* values. For a deeper check,
connect the interface's OUT to its IN and compare send and receive timestamps with a MIDI monitor.
Report results in an issue so they can inform the scheduler benchmark ADR.
