# 3. Idempotency is a unique constraint, not application code

Status: accepted
Date: 2026-08-28

## Context

The broker guarantees at-least-once delivery. Every consumer will eventually see the same message twice: a
redelivery timeout, a crash between the business write and the acknowledgement, a consumer-group rebalance
during a deploy. For a consumer that charges a card, that is a second charge.

The obvious implementation is to check whether a message has been handled and skip it if so.

## Decision

A `processed_messages(consumer_name, message_id)` table with the pair as its primary key. Consumers insert
into it as part of the same `SaveChangesAsync` as their business change, and treat a unique-constraint
violation as "already done".

Not a check followed by a write. Two concurrent redeliveries both pass a check and both proceed, and that
is precisely the race that produces the double charge. Letting the database refuse the second insert
removes it, because the constraint is enforced by the one component able to serialise the two.

The consumer name is part of the key because two consumers may legitimately handle the same message. Here
the timeline projection and the saga both react to `PaymentAuthorized`, and neither should suppress the
other.

## Consequences

- The business change, the outbox rows it produces, and the ledger entry commit together. There is no
  interval in which the effect exists but the record of it does not, so a crash cannot replay it.
- Ledger retention has to outlive the broker's maximum redelivery window. Purging an entry while the broker
  can still redeliver its message reopens the exact hole the ledger closes. The two settings sit next to
  each other in `OutboxOptions` with that written down, and the runbook repeats it.
- Business tables carry their own unique constraints as a second line: one payment per order, one
  reservation per order, one shipment per order. If the ledger were ever bypassed, the database still
  refuses rather than charging twice.
- A unique-constraint violation from a business table is also treated as a duplicate. Both mean the work has
  already happened, which is the honest reading, but it does mean a genuine constraint bug would be logged
  as a duplicate rather than raised. Noted rather than solved.
