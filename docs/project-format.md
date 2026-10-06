# Cadence project format

A Cadence project (`.cadence`) is a single UTF-8 JSON document. It is meant to be readable, to diff
cleanly in version control, and to stay portable: it contains no machine-local paths and no port
indices, only stable identifiers and identity hints.

```json
{
  "format": "cadence-project",
  "formatVersion": 2,
  "project": {
    "id": "0199b0f2-…",
    "name": "Demo",
    "loop": { "start": 0, "end": 7680 },
    "sequence": {
      "ppqn": 960,
      "tempo":   [ { "tick": 0, "microsecondsPerQuarter": 500000 } ],
      "meter":   [ { "tick": 0, "numerator": 4, "denominator": 4 } ],
      "markers": [ { "tick": 3840, "name": "Verse" } ],
      "tracks": [
        { "id": "…", "name": "Bass", "muted": false, "soloed": false,
          "events": [
            { "id": "…", "tick": 0, "type": "program", "channel": 2, "program": 34, "bankMsb": 0, "bankLsb": 0 },
            { "id": "…", "tick": 0, "type": "controller", "channel": 2, "controller": 7, "value": 3355443200 },
            { "id": "…", "tick": 0, "type": "note", "length": 480, "channel": 2, "note": 40, "velocity": 100, "release": 64 },
            { "id": "…", "tick": 0, "type": "sysex", "bytes": "F0 43 10 4C 00 00 7E 00 F7" }
          ] }
      ]
    },
    "routing": [
      { "track": "…",
        "profile":  { "id": "cadence.generic.xg", "name": "Generic XG" },
        "endpoint": { "provider": "coremidi", "key": "-12345", "name": "QY Out" },
        "channel": 3, "transpose": -12,
        "voice": { "bank": "normal", "program": 34 } }
    ]
  },
  "extensions": { "org.example.feature": { } }
}
```

## Rules

- Channels and program numbers are one-based (1–16, 1–128), as musicians see them. Ticks are absolute.
- Channel events describe what is played, not MIDI 1.0 bytes:
  - `note`: a note with its length, attack velocity, and release velocity.
  - `noteOff`: a release with no matching note, kept from an import.
  - `controller`: a controller number (0–127) and its value.
  - `program`: a program, with optional `bankMsb` and `bankLsb` (0–127).
  - `pitchBend`, `channelPressure`, and `polyPressure` (which also has a `note`): a value.
- Controller, pitch bend, and pressure `value`s are 32-bit (0–4294967295), the MIDI 2.0 resolution.
  A MIDI 1.0 value is scaled up so that it scales back down exactly: 0 is 0, the centre (64, or
  8192 for pitch bend) is 2147483648, and 127 (or 16383) is 4294967295.
- The other event types are `sysex` (a complete `F0 … F7` message), `raw` (bytes kept verbatim from
  an import), and `meta` (a preserved meta event).
- Routes are stored whether or not their profile or endpoint is currently available.
- `extensions` and any other unrecognised top-level properties are preserved on save.
- Reading is strict: any invalid value fails with the JSON path of the problem.

## Versions and migrations

`formatVersion` increases whenever the structure changes. Older files are upgraded in memory by a
chain of migrations before they are read; saving always writes the current version. A file from a
newer Cadence is refused with an explanation and never overwritten.

Format 2 replaced format 1's `channel` events (raw MIDI 1.0 bytes) with the channel event types
above. Format 1 files are not migrated; import the original MIDI file again instead.

## Saving and recovery

1. The whole file is written to a hidden temporary file beside the project, flushed to disk, and
   read back to verify it.
2. The previous project file is copied to `<name>.cadence.bak`.
3. The temporary file is renamed over the project (an atomic replace on the same volume).

If the project file is damaged or missing, Cadence opens the backup and says so. Autosaves go to
`<name>.cadence.recovery`; when that file is newer than the project, Cadence offers to restore it.
