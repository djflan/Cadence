# 0015. System exclusive through WinMM without blocking playback

- Status: Accepted
- Date: 2026-10-04

## Context

ADR 0014 shipped the WinMM output adapter without system exclusive, which device profiles such as
the QY100 need. WinMM sends SysEx with `midiOutLongMsg`. The message buffer and its `MIDIHDR`
must stay at a fixed address, prepared with `midiOutPrepareHeader`, until the driver sets
`MHDR_DONE`. Only then may it be unprepared and reused. `IMidiOutput.Send` runs on the playback
thread and must not block or allocate per call.

## Decision

- Each open WinMM output owns a **pool of 8 long-message buffers** in native memory (not on the
  managed heap, so the driver's pointers stay valid). Each buffer starts at 1 KB and grows only
  when a larger message than it has held before arrives, up to the 64 KB limit shared with
  CoreMIDI.
- `Send` with SysEx **claims a free buffer without waiting**: a buffer is free if it was never queued,
  or if the driver has marked it done, in which case it is unprepared first. It is then copied,
  prepared and queued. If all 8 are still in the driver, `Send` returns `QueueFull`, the existing
  result for a full adapter buffer.
- Completion is found by **polling `MHDR_DONE`** on the next SysEx send rather than by a driver
  callback, so no callback thread or locking is involved.
- `Dispose` resets the device (which marks every queued buffer done), unprepares the buffers that
  were queued, closes the device, then frees the memory.
- WinMM endpoints now report `EndpointCapabilities.SystemExclusive`.

## Consequences

- A burst of more than 8 SysEx messages faster than the device accepts them reports `QueueFull`
  for the extras. At 31,250 baud a 1 KB dump takes about 330 ms on a hardware port, so large dumps
  should be paced by the caller. This becomes relevant with bulk dumps to the QY100.
- WinMM does not document whether a short message sent while a long message is still in the
  driver can overtake it. Verify ordering on hardware with the QY100 (docs/hardware-test-plan.md).
- Each open output uses about 8 KB of native memory, more only after large messages.
