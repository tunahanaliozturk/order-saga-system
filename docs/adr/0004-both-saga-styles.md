# 4. Both coordination strategies, on the same services

Status: accepted
Date: 2026-08-28

## Context

Orchestration and choreography are the two ways to coordinate a business transaction across services, and
the trade-off between them is a standard interview question with a standard non-answer: orchestration is
easier to debug, choreography is less coupled, it depends.

Building one of them proves the pattern. Building both, for the same business flow, on the same four
services, is what makes the comparison mean anything.

## Decision

Both run at once. Which one handles an order is decided by the route that created it, and every message
carries the variant so a consumer belonging to only one flow can say so.

The orchestrator is triggered by its own message, `StartOrderSaga`, rather than by filtering `OrderCreated`
on the variant. The first version filtered, and MassTransit's saga repository creates an instance before
the filter is evaluated, so every choreographed order left an empty saga row behind and failed on a
not-null column. A separate trigger means the orchestrator never sees an order that is not its to run.

## Consequences

What the code now shows, rather than asserts:

- **Coupling is real and visible.** `OrderStateMachine` names all three downstream services. No participant
  in the choreographed flow names any other.
- **Choreography pushes state into participants.** `PaymentAuthorized` does not carry order lines, and the
  Inventory service may not read the Order service's database, so it persists what it heard from
  `OrderCreated` in a table of its own. The orchestrator needs none of that: it already holds the lines.
- **Choreography inherits races the orchestrator does not have.** Nothing guarantees Inventory processed
  `OrderCreated` before Payment published `PaymentAuthorized`. The consumer throws and lets the transport
  redeliver, which works, but the race exists only because there is no coordinator.
- **Some operations need a coordinator.** Unwinding an already-completed order is an orchestrated-only
  endpoint, and the choreographed route answers 409. That is a limitation of the pattern rather than of
  this implementation, and saying so is more useful than pretending otherwise.
- **The two must not diverge quietly.** A contract suite runs the same scenarios against both routes and
  compares the resulting timelines, so "they behave equivalently" fails a build when it stops being true.

The cost is that both flows share a broker, which is why every message carries its variant. Without it the
choreographed refund subscriber would also fire for orchestrated orders, whose refunds the state machine is
already commanding, and the customer would be refunded twice by two mechanisms that each believe they are
the only one running.
