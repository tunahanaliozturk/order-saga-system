# 5. One order at a time, all orders in parallel

Status: accepted
Date: 2026-08-28

## Context

Consumers process messages concurrently by default. That is what you want for throughput and wrong for
correctness when two of those messages concern the same order.

Two things broke because of it. The timeline recorded events in whatever sequence their transactions
happened to commit, so an audit trail meant to explain what happened sometimes disagreed with what
happened. And two events reaching the same saga instance at once collided on the concurrency token, forcing
a retry that looked like a transient fault and was really contention we had created.

## Decision

`UsePartitioner(16, context => context.CorrelationId ?? Guid.Empty)` on the consume pipeline of every
service.

Every message for one order lands on one partition and is handled one at a time, in delivery order.
Different orders run in parallel across the other fifteen. The correlation id is the order id, and the
relay sets it on every message it publishes, so there is always a value to partition on.

## Consequences

- The timeline is ordered because the events were recorded in order, not because it sorts them afterwards.
- Saga instances stop contending with themselves, so the optimistic concurrency token now catches only what
  it is for: two genuinely concurrent writers.
- Per-service parallelism is capped at the partition count. Sixteen suits a demo; a real deployment sizes
  it from the consumer's concurrency budget, and raising it costs little but memory.
- A slow order holds its own partition and nothing else. That is the trade every ordered-stream design
  makes, and it is why the partition key is the order rather than something coarser.
