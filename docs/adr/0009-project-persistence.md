# 0009. Project persistence: versioned JSON with atomic saves

- Status: Accepted
- Date: 2026-10-04

## Context

Projects must be intelligible, portable, versionable, and recoverable. A crash or full disk during
save must never destroy the last good version, and a project from a newer Cadence must never be
silently downgraded.

## Decision

- **Format.** A single indented JSON document (`cadence-project`, `formatVersion` 1) written
  deterministically. Bytes are space-separated hex so SysEx and raw data stay reviewable. A
  container (zip) was rejected for now: projects contain no binary assets, and plain JSON diffs well.
- **Strict reading** with JSON paths in every error; domain validation failures are converted to
  format errors at the path being read. Unknown top-level properties and `extensions` round-trip.
- **Migrations** operate on the JSON tree (`IProjectMigration`), one version step at a time, before
  the model is read. Newer versions raise `ProjectVersionException`.
- **Atomic save.** Write a temporary sibling with write-through, flush, read back and verify, copy
  the old file to `.bak`, then rename over the target. Failures and cancellation delete the
  temporary file and leave the project untouched.
- **Recovery.** A damaged or missing project opens from `.bak` with an explicit notice. A newer-format
  file is never replaced by its backup. Autosaves are written atomically to `.recovery`.

## Consequences

- Very large projects (hundreds of thousands of notes) produce large files; a compact event
  encoding can be introduced as a new format version if measurements warrant it.
- Directory entries are not fsynced after the rename (.NET exposes no portable API); on power loss
  some file systems may surface the previous version, which is still a consistent project.
