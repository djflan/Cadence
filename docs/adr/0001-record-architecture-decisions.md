# 0001. Record architecture decisions

- Status: Accepted
- Date: 2026-10-04

## Context

Cadence makes several decisions that are expensive to reverse: project-file and profile schema shapes,
timing and scheduling strategy, whether to introduce native code, and which UI framework to adopt.
Contributors need to understand why those choices were made, not only what the code does.

## Decision

Record consequential decisions as numbered Markdown ADRs in `docs/adr/`, indexed in `docs/adr/README.md`.
A decision that relies on performance claims must cite the measurement that justified it.

## Consequences

Decisions are reviewable in pull requests alongside the code they shape. Reversing a decision means
writing a superseding ADR, which keeps history honest at the cost of a little ceremony.
