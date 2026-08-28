# 2. A hand-rolled outbox rather than the one in the box

Status: accepted
Date: 2026-08-28

## Context

MassTransit ships a transactional outbox. `AddEntityFrameworkOutbox`, a bus outbox filter, and the
dual-write problem is handled. Writing one by hand is a few hundred lines that already exist in a library
this project already depends on.

## Decision

The outbox here is hand-written: an `outbox_messages` table, a relay that claims rows with
`FOR UPDATE SKIP LOCKED`, and publication that marks a row only after the broker acknowledges.

The outbox is the subject of this project, not a detail of it, and a configuration block does not
demonstrate that the dual-write problem is understood. What matters is visible in code someone can read:
what the claim query locks, when a row is marked published, what happens to a row whose publish throws, and
why the message id on the bus is the outbox row id rather than a freshly generated one.

Two things came out of writing it that a library would have hidden:

- The relay must start after the bus. Hosted services start in registration order, and a relay that starts
  first publishes into exchanges whose queues do not exist yet. RabbitMQ discards those messages without
  raising anything anywhere. That bug was live in this repository, and it took a suite that lost one event
  in ten runs to find it.
- `MassTransitHostOptions.WaitUntilStarted` defaults to false, so a host reports itself started before its
  topology exists. Same failure, different cause, and the same silence.

Both are properties of the system rather than of the outbox implementation, and both would have been just
as true with the library. Building it is what surfaced them.

## Consequences

- Retention, failure recording, and relay metrics are ours to build. They were going to be anyway.
- Several instances of a service can run the relay at once without publishing a row twice, because of
  `SKIP LOCKED`. That is tested against real Postgres, which is the only place it can be tested honestly.
- A crash between publish and commit republishes rather than loses. Duplicates are cheap here because every
  consumer is idempotent; a lost message is not cheap at all.
- If the goal were to ship this rather than to explain it, the library version is the right answer.
