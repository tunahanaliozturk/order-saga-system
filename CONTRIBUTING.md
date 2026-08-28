# Contributing

## Getting set up

You need the .NET 10 SDK and Docker. Nothing else.

```bash
dotnet restore
dotnet build
dotnet run --project tests/OrderSaga.UnitTests
dotnet run --project tests/OrderSaga.IntegrationTests   # needs Docker running
```

`dotnet run --project src/OrderSaga.AppHost` brings all four services up locally with a dashboard.

## Ground rules

**Warnings are errors.** `TreatWarningsAsErrors` is on for every project, and CI runs
`dotnet format --verify-no-changes`. Run `dotnet format` before you push.

**A change to a guarantee needs a test that would fail without it.** The promises in the README are the
point of the project. If you touch the outbox, the idempotency ledger, a compensation path, or the ordering
guarantee, the pull request should include the test that proves the new behaviour, not just one that passes.

**Both saga variants stay in step.** Any change to the order flow has to work for orchestration and
choreography, and the contract suite compares them. If they genuinely have to differ, say so in the test
rather than loosening the comparison.

**Tests run against real infrastructure.** Postgres and RabbitMQ come from Testcontainers, and all four
services run as real hosts. Please do not replace them with in-memory doubles: an in-memory bus does not
redeliver on a crash and an in-memory database does not enforce the unique constraint that is the actual
idempotency mechanism.

**Explain the why, not the what.** Comments in this repository exist where a reader would reasonably ask
"why is it done this way", usually because the obvious approach is wrong for a non-obvious reason. Comments
that restate the code get removed in review.

**Decisions with a real trade-off get an ADR.** Short, in `docs/adr/`, following the existing five:
context, decision, consequences. Include the option you did not take and why.

## Commits and pull requests

Present tense, one concern per commit, and a body when the reason is not obvious from the diff. Pull
requests should say what changed, why, and how you know it works.
