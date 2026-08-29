# 6. Permissive dependencies only, checked by the build

Status: accepted
Date: 2026-08-29

## Context

Picking MassTransit 8 over 9 to stay on Apache-2.0 ([ADR 1](0001-masstransit-8-not-9.md)) turned out to be
the easy half of the problem. A licence is a property of the whole dependency tree, and most of that tree is
not chosen: it arrives transitively, its terms live in a file inside the `.nupkg`, and nothing in a normal
build mentions any of it. The package restores, the code compiles, and that is the end of the feedback.

Auditing this repository found two dependencies that were not permissively licensed, neither of them
obvious:

- **NBomber 6.6.0**, used for the load harness, ships an "NBomber License Agreement v3.0" requiring a paid
  commercial subscription, and its section 2.3(c) forbids distributing the software as part of any product.
  On nuget.org it shows only a deprecated licence URL, so it looks unremarkable in a package list.
- **JsonPatch.Net 5.0.2**, three levels below `Aspire.Hosting`, ships an Open Source Maintenance Fee
  agreement. The code is MIT and the agreement says so, but it asks revenue-generating users above
  US$10,000 a year to pay a monthly fee for the pre-built binaries. Nobody adds that dependency on purpose;
  it comes with Aspire.

## Decision

Only permissive licences, and the build checks rather than trusts.

**NBomber is gone.** The load harness is written by hand: an open-model request loop, a percentile
function, and a pass that measures saga completion separately from API acceptance. It is about eighty lines
and has no package references at all. Measuring is not the hard part of this project, and a load generator
is not worth a licence question.

**Aspire is pinned to 13.4.6**, the last release before `Aspire.Hosting` moved to JsonPatch.Net 5. That is
a patch-level step within the same major, so the local orchestration story is unchanged.

**`tools/OrderSaga.LicenseAudit` runs on every build.** It resolves the full tree with
`dotnet list package --include-transitive`, then reads each package's terms from the restored `.nupkg`
directory: the SPDX expression when there is one, the licence file when the package ships one, and any
licence file it can find when the package predates SPDX expressions and only carries a `licenseUrl`.
Anything it cannot match against a permissive licence fails the build with the package name and the first
line of what it found.

## Consequences

- 160 packages, every one permissive, and it stays that way without anyone remembering to look.
- The audit is offline. Everything it reads is on disk after a restore, so it behaves the same in CI as
  behind a proxy that blocks nuget.org.
- The allow list is a small set of SPDX identifiers in the tool itself. Adding one is a code change with a
  diff and a reviewer, which is the right amount of friction.
- Unreadable is treated as not permissive. A package that declares nothing and ships nothing readable fails,
  because "I could not tell" and "it is fine" are different answers.
- A routine dependency bump can now fail on licence terms rather than on compilation. That is the point.
