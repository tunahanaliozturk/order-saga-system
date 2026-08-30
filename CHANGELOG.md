# Changelog

Notable changes to this project. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-08-29

First release. One order flow implemented twice, as an orchestrated saga and as a choreographed one, across
four services that share nothing but a message contract.

### What it does

- `POST /orders` starts the orchestrated flow, where a state machine tells each service what to do next.
  `POST /orders/choreographed` starts the same business flow with no coordinator at all: each service reacts
  to the previous one's event and knows nothing about what runs after it.
- Every service owns its own database. No service reads another's schema and no service calls another's
  API: every interaction is a message.
- Whatever committed is undone in reverse from wherever the chain broke. A declined payment compensates
  nothing, because nothing downstream has run yet; a failed shipment undoes both the reservation and the
  payment.
- `GET /orders/{id}/timeline` is the cross-service story of one order, built as a projection because
  database-per-service means no other service could write it.
- An order that stops making progress is flagged by a sweep and surfaced on `GET /orders/stuck`, with
  `POST /orders/{id}/retry` to re-drive the step it is waiting on.

### The two mechanisms that make it correct

- **Transactional outbox.** The event row commits with the business change or not at all, so there is no
  window in which a customer was charged and nobody will ever be told to ship.
- **Idempotency ledger.** A unique constraint on `(consumer, message_id)`, written in the same transaction
  as the effect. A duplicate is detected by the database refusing the second insert, not by a preceding
  read that two concurrent redeliveries could both pass.

### Proven, not asserted

Against real Postgres and real RabbitMQ:

- Ten concurrent copies of one message produce exactly one charge.
- A message staged by a process killed before it published is still published after restart.
- The orchestrator can be killed mid-saga and resumes from persisted state.
- Both coordination strategies produce an equivalent timeline across the same scenario matrix.
- An order that stops making progress is flagged rather than forgotten.

### Notes

- MassTransit 8.5, not 9, because 9 moved to a commercial licence. Every package in the tree is
  permissively licensed, enforced on every build.
- The comparison this repository exists to make is in the README: what orchestration costs in coupling,
  and what choreography costs in state every participant has to keep for itself.

[1.0.0]: https://github.com/tunahanaliozturk/order-saga-system/releases/tag/v1.0.0
