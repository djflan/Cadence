# 0002. Build and test conventions

- Status: Accepted
- Date: 2026-10-04

## Context

Cadence needs reproducible builds across macOS, Windows, and Linux, strict compiler feedback, and a
default test suite that never depends on MIDI hardware or wall-clock timing.

## Decision

- **Runtime:** .NET 10 (LTS), C# `latest`. `global.json` pins SDK 10.0.300 with `latestFeature` roll-forward.
- **Shared settings:** `src/Directory.Build.props` enables nullable reference types, strict features,
  deterministic builds, `latest-recommended` analyzers, and code-style enforcement during build, with
  warnings treated as errors. Missing-XML-doc warnings (CS1591) are off: documentation is expected where
  intent, units, ownership, or timing are not obvious, not on every member.
- **Formatting:** `.editorconfig` is the source of truth; `dotnet format --verify-no-changes` must pass.
- **Dependencies:** central package management in `src/Directory.Packages.props` with transitive pinning.
- **Tests:** xUnit v3 on Microsoft.Testing.Platform (opted in via `global.json`, required by xUnit v3 4.x on
  the .NET 10 SDK). CsCheck provides property-based and fuzz-style tests; it is framework-agnostic and has
  no F# runtime dependency, unlike FsCheck.
- **Test projects:** `Cadence.Tests.Unit` is fast and deterministic. Tests that touch the real file system
  or OS services go in `Cadence.Tests.Integration` once needed. Hardware-dependent checks are opt-in and
  never part of the default suite.
- **Boundaries:** architecture tests assert project dependency direction (for example, that
  `Cadence.Domain` references only the base class library).

## Consequences

Analyzer upgrades can break the build when the SDK rolls forward; that is intended, and suppressions
require a written rationale. The new `dotnet test` experience differs from VSTest (`--filter` is replaced
by framework-specific options such as `--filter-method`), which contributors must learn.
