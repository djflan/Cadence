# Device profile format (schema version 1)

A device profile is a JSON file named `<name>.cadence-profile.json` that describes what an
instrument understands. Profiles never say where an instrument is connected: the same profile can
drive a physical instrument, a compatible software synth, or a virtual port. Comments and trailing
commas are allowed.

Profiles are untrusted input. Cadence rejects any profile with an error and reports every problem
with its JSON path, for example `$.banks[0].programs[3].number: must be between 1 and 128 (found 0)`.
Warnings (unknown properties, unused template parameters) do not block loading.

## Top level

| Property | Required | Description |
| -------- | -------- | ----------- |
| `format` | yes | Always `"cadence-device-profile"`. |
| `schemaVersion` | yes | `1`. Newer versions are refused with a clear message. |
| `id` | yes | Stable identifier: lowercase letters and digits separated by `.` or `-`, e.g. `community.vendor.model`. Projects refer to profiles by this ID. |
| `version` | yes | The profile content's own version, e.g. `"1.2.0"`. |
| `name` | yes | Display name (≤ 128 characters). |
| `manufacturer`, `model`, `description` | no | Display text. |
| `notices` | no | Trademark and non-affiliation notices, shown wherever the profile is described. |
| `provenance` | yes | Where the data came from (below). |
| `protocols` | no | Any of `gm1`, `gm2`, `gs`, `xg`, `custom`. |
| `identity` | no | MIDI Identity Reply match rules: `manufacturerId` (1 byte, or 3 bytes starting `00`), optional 2-byte `family` and `member`, as hex strings such as `"00 20 29"`. |
| `drumChannels` | no | Channels (1–16) that play drum kits by default. |
| `banks` | no | Up to 1024 banks (below). |
| `drumKits` | no | Kits with per-note instrument names. |
| `controllers` | no | Control change numbers the instrument responds to, with optional defaults. |
| `parameters` | no | Registered parameters (RPN): `msb`, `lsb`, `name`, `min`, `max`, optional `default`, as 14-bit values. |
| `sysex` | no | Up to 256 SysEx templates (below). |
| `initialization` | no | Templates to send, in order, **only when the user explicitly initializes the instrument**: `{ "sysex": "<template id>", "delayAfterMs": 50 }`. |
| `limitations` | no | Known gaps, in plain language. |
| `extensions` | no | Custom data under namespaced keys (`"org.example.feature"`), preserved but not interpreted. |

## Provenance

| Property | Description |
| -------- | ----------- |
| `sources` | At least one source for the data. |
| `contributors` | Who compiled it. |
| `license` | License of the profile data, e.g. `"MIT"`. |
| `redistributionConfirmed` | Must be `true`. |
| `verification` | `unverified` (from documentation), `simulated` (checked against traces or a compatible synth), or `hardware-verified`. |
| `notes` | Optional details. |

## Banks and programs

```json
{ "id": "drums", "name": "Drum kits", "kind": "drums", "msb": 127, "lsb": 0,
  "programs": [ { "number": 1, "name": "Standard", "category": "Kits" } ] }
```

`msb` and `lsb` are the bank select values (0–127); omit them for instruments without banks.
Program `number` is **one-based (1–128)**, as printed in most voice lists; Cadence sends
`number − 1` on the wire. Selecting a program over MIDI 1.0 sends bank select MSB, then LSB, then the program change.

## SysEx templates

```json
{ "id": "xg-system-on", "name": "XG System On", "effect": "reset",
  "bytes": "F0 43 {device} 4C 00 00 7E 00 F7",
  "parameters": [ { "name": "device", "min": 16, "max": 31, "default": 16 } ] }
```

- `bytes` is space-separated: two-digit hex bytes or `{parameter}` placeholders. It must start with
  `F0`, end with `F7`, and every byte between must be a data byte (`00`–`7F`). At most 512 bytes.
- Each placeholder is a declared 7-bit parameter with `min ≤ default ≤ max`.
- `effect` is `parameter`, `reset`, or `bulk`. Resets and bulk transfers always require confirmation
  before sending. Opening a project never sends anything from a profile.

Version 1 does not compute checksums (as used by some vendors' parameter messages) or multi-byte
values; such messages need a later schema version.
