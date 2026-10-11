# 0029. The project is renamed Bluestone, with no compatibility for Cadence files

- Status: Accepted
- Date: 2026-10-10

## Context

The project was called Cadence. That name is crowded: KXStudio already ships Linux audio tools called Cadence,
and Cadence Design Systems is a large software company. Bluestone is unused in audio and MIDI software, and it
fits the project: some of the Stonehenge bluestones ring when struck, and may have been chosen for their sound.
The program has never been released, and the only Cadence files that exist are the maintainer's own.

## Decision

- **Every use of the name changes.** Repository, solution, projects, assemblies, namespaces, the window title,
  menus, About text, theme keys, docs, and the licence say Bluestone.
- **File formats change with no compatibility.** Projects are `.bluestone` (`bluestone-project`), chain presets
  `.bluestone-chain` (`bluestone-chain-preset`), and device profiles `.bluestone-profile.json`
  (`bluestone-device-profile`). Built-in IDs use the `bluestone.` prefix (`bluestone.generic.gm1`,
  `bluestone.midi.transpose`, `bluestone.reference`). Cadence files are refused as "not a Bluestone project"; there
  is no alias, reader, or migration for them.
- **Names other software sees change.** The MIDI ports are Bluestone Out, Bluestone In, and Bluestone Monitor, the
  ALSA client is Bluestone, and the app data folder is `Bluestone`. Nothing moves the old folder.
- **ADRs keep their decisions.** Earlier ADRs now use the new name and file names; no decision in them changed.
- **History stays.** Commit messages still say Cadence, and so does the copyright text stored inside the frozen
  format 2 and 3 Canon project fixtures. The samples are current: the demo song's embedded title is Bluestone
  Demo, and the Canon files are regenerated with Bluestone in their copyright text.

## Consequences

- One rename, no compatibility code. A Cadence project can be converted by hand: rename it and replace the
  `cadence` format name and IDs with `bluestone`.
- Other apps connected to the Cadence virtual ports have to be connected to the Bluestone ports again; on macOS
  the ports' unique IDs change with their names.
