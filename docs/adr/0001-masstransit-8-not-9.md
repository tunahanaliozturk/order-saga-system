# 1. MassTransit 8, not 9

Status: accepted
Date: 2026-08-28

## Context

MassTransit 9.2 is the current release, and version 9 changed the licence. Those packages no longer carry
an SPDX expression; they point at `https://massient.com/license`, and use above a revenue threshold is
commercial. MassTransit 8.5.10 is still published under Apache-2.0.

This repository is MIT and public. Anyone who clones it inherits whatever its dependencies require, so a
dependency with commercial terms is a decision about them, not only about me.

## Decision

Pin MassTransit 8.5.10 across all three packages.

This is not a compromise on currency. 8.5.10 was published in October 2025, ships a native `net10.0`
target, and depends on EF Core 10, so nothing here runs on an older framework to accommodate it.
Everything this project needs is in version 8: the state machine, the Entity Framework saga repository
with optimistic concurrency, retry and redelivery filters, OpenTelemetry propagation, and the in-memory
test harness.

## Consequences

- The dependency tree stays permissively licensed, which is the point of publishing this at all.
- New MassTransit features land in 9 and this repository will not get them. Nothing planned here needs any.
- The version is pinned in `Directory.Packages.props` with the reasoning beside it, so a routine dependency
  bump does not quietly cross a licence boundary.
- For a commercial system rather than a public reference the calculation would be different, and version 9
  would likely be worth paying for.
