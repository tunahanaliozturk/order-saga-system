# Security

## Reporting a vulnerability

Open a [private security advisory](https://github.com/tunahanaliozturk/order-saga-system/security/advisories/new)
rather than a public issue. I will acknowledge within a few days and keep you updated.

## What this project is, and is not

This is a reference implementation of the saga pattern. It is not a hardened service, and two things are
deliberately absent:

- **No authentication anywhere.** The services trust each other and the network between them, and the order
  API is open. A real deployment puts a service-to-service token on every message and a user token on the
  public route.
- **The fault-injection endpoints are unauthenticated on purpose.** `POST /test/{service}/fault-rate` will
  make a service fail on demand. It exists so the chaos suite exercises the same code the demo runs, and it
  must not be exposed anywhere that matters.

The compose file ships fixed credentials for Postgres and RabbitMQ. They are demo credentials in a demo
file and are not a finding.

## What is in scope

**Correctness of the idempotency ledger.** Anything that lets a redelivered message apply its effect twice
is a real vulnerability in a system that charges cards, whatever the demo status. The same goes for a way
to bypass the unique constraints on the business tables.

**Cross-service data access.** No service should be able to read or write another's schema. A change that
introduces a cross-database query breaks the guarantee the whole design rests on.

**Payload contents.** Messages carry a customer id and never a name, an address, or a payment instrument.
Personally identifying data stays in the service that owns it. A contract change that puts any of it on the
bus is worth flagging.

**Unbounded growth.** The outbox and the idempotency ledger both have retention sweeps, and the ledger's
window is coupled to the broker's redelivery window. A change that breaks either sweep turns into an
availability problem rather than a tidiness one.
