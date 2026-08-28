# Runbook

What to do when an order is not doing what it should.

## Is anything stuck?

```bash
curl -s localhost:5000/orders/stuck | jq
```

An order is stuck when it has not reached `Completed` or `Cancelled` within the configured timeout, five
minutes by default. The endpoint answers from two sources: the flag a background sweep sets, and the clock,
so an order that went overdue since the last sweep still shows up rather than waiting a sweep interval to
become visible.

Stuck is a flag, not a status. The order keeps whatever state it reached, which is the only clue about
where it stopped.

## What happened to this order?

```bash
curl -s localhost:5000/orders/{id}/timeline | jq
```

One row per thing that happened, in the order it happened, with the event body as it arrived and the name
of the service that reported it. This is the whole reason the timeline exists: reconstructing an order from
four services' logs at three in the morning is not a plan.

Events for one order are processed one at a time, so this ordering is real rather than approximate. See
[ADR 5](adr/0005-ordering-per-correlation-id.md).

If the timeline stops after `PaymentAuthorized`, the Inventory service is the one to look at. If it stops
after `OrderCreated` on an orchestrated order, the orchestrator never picked it up, which usually means the
Order service was down when the order was placed.

## Nudging an order

```bash
curl -s -X POST localhost:5000/orders/{id}/retry
```

Re-publishes the message the current step is waiting on. It is safe to call more than once: every consumer
is idempotent, so a step that already ran absorbs the repeat instead of running twice. There is a test that
sends three nudges and asserts one charge.

It is a nudge, not a restart. Nothing re-runs the saga from the beginning, because that would turn a real
bug into a loop that looks healthy.

Retry is refused on a terminal order with 409.

## Unwinding a completed order

```bash
curl -s -X POST localhost:5000/orders/{id}/cancel \
  -H 'Content-Type: application/json' \
  -d '{"reason":"Customer changed their mind."}'
```

Cancels the shipment, releases the stock, and refunds the payment, in that order, and marks the order
cancelled once all three confirm.

Only works on orchestrated orders. The choreographed route answers 409, because unwinding a finished
transaction needs a coordinator and choreography does not have one. That is a property of the pattern, not
a gap in the implementation.

## Turning on a failure

Every service has a fault dial. It is a runtime knob rather than a build flag, so the code exercised by the
chaos tests is the same code that runs in the demo.

```bash
# Decline every payment.
curl -s -X POST localhost:5001/test/payment/fault-rate \
  -H 'Content-Type: application/json' -d '{"rate":1.0,"mode":0}'

# Fail every shipment. This is the interesting one: two committed steps have to be undone.
curl -s -X POST localhost:5003/test/shipping/fault-rate \
  -H 'Content-Type: application/json' -d '{"rate":1.0,"mode":0}'

# Back to normal.
curl -s -X POST localhost:5001/test/payment/fault-rate \
  -H 'Content-Type: application/json' -d '{"rate":0,"mode":0}'
```

`mode` is `0` for a business decline, `1` for a timeout, `2` for a dropped connection. The distinction is
the point: a decline is a business outcome the saga compensates for, while a timeout or a reset is a
transport failure the broker retries. Both have to work, and they work differently.

Stock is separate from the fault dial, because running out of stock is a real business outcome rather than
an injected one:

```bash
curl -s -X PUT localhost:5002/inventory/stock/{sku} \
  -H 'Content-Type: application/json' -d '{"quantity":0}'
```

## The two retention settings that are coupled

`OrderSaga:Outbox:ProcessedRetention` must comfortably exceed the broker's maximum redelivery window.

The idempotency ledger is what stops a redelivered message from charging a customer twice. Purge an entry
while the broker can still redeliver its message and the effect happens again. Thirty days against
RabbitMQ's default behaviour is generous; if you shorten one, check the other.

`OrderSaga:Outbox:PublishedRetention` is not coupled to anything. A published row has done its job.

## Where the queues are

RabbitMQ management is on <http://localhost:15672>, user `saga`, password `saga`.

A queue with a growing depth means its consumer is down or slow. A `_error` queue with anything in it means
messages exhausted their retries, which is worth looking at immediately: every consumer here is written so
that ordinary failures are business outcomes, not exceptions, so an error queue entry is a bug rather than
a bad day.
