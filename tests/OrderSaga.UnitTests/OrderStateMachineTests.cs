using OrderSaga.Contracts;
using OrderSaga.OrderService;
using Shouldly;

namespace OrderSaga.UnitTests;

/// <summary>
/// Every transition the orchestrator can make, including the ones that only happen when something breaks.
/// </summary>
/// <remarks>
/// A saga tested only along the happy path is a saga whose compensation code has never run. Each failure
/// point gets its own test asserting the exact set of compensations, because "some compensation fired" is
/// not the same promise as "the two steps that committed were undone and the one that did not was left
/// alone".
/// </remarks>
public sealed class OrderStateMachineTests
{
    [Fact]
    public async Task An_order_walks_from_created_to_completed()
    {
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId, total: 42m);

        // Nothing is sent directly. The saga stages its commands so that advancing its own state and
        // telling the next service are one transaction.
        AuthorizePayment authorize = context.Outbox.Of<AuthorizePayment>().ShouldHaveSingleItem();
        authorize.CorrelationId.ShouldBe(orderId);
        authorize.Amount.ShouldBe(42m);
        (await context.Saga.Exists(orderId, machine => machine.AwaitingPayment)).ShouldNotBeNull();

        Guid paymentId = Guid.CreateVersion7();
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 42m));

        context.Outbox.Of<ReserveInventory>().ShouldHaveSingleItem();
        (await context.Saga.Exists(orderId, machine => machine.AwaitingInventory)).ShouldNotBeNull();

        Guid reservationId = Guid.CreateVersion7();
        await context.DeliverAsync(new InventoryReserved(orderId, SagaVariant.Orchestrated, reservationId));

        context.Outbox.Of<ScheduleShipment>().ShouldHaveSingleItem();
        (await context.Saga.Exists(orderId, machine => machine.AwaitingShipment)).ShouldNotBeNull();

        await context.DeliverAsync(
            new ShipmentScheduled(orderId, SagaVariant.Orchestrated, Guid.CreateVersion7()));

        (await context.Saga.Exists(orderId, machine => machine.Completed)).ShouldNotBeNull();

        // Nothing was undone, because nothing failed.
        context.Outbox.Of<RefundPayment>().ShouldBeEmpty();
        context.Outbox.Of<ReleaseInventory>().ShouldBeEmpty();
        context.Outbox.Of<CancelShipment>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_declined_payment_cancels_the_order_and_compensates_nothing()
    {
        // The one failure path with no compensation: nothing downstream has committed yet. A saga that
        // fires a refund here would be refunding a payment that was never taken.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId);
        await context.DeliverAsync(
            new PaymentDeclined(orderId, SagaVariant.Orchestrated, "Card issuer declined."));

        (await context.Saga.Exists(orderId, machine => machine.Cancelled)).ShouldNotBeNull();

        context.Outbox.Of<RefundPayment>().ShouldBeEmpty();
        context.Outbox.Of<ReleaseInventory>().ShouldBeEmpty();
        context.Outbox.Of<ReserveInventory>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Unavailable_stock_refunds_the_payment_and_releases_nothing()
    {
        // Exactly one step committed, so exactly one step is undone.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();
        Guid paymentId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId);
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 100m));
        await context.DeliverAsync(
            new InventoryUnavailable(orderId, SagaVariant.Orchestrated, [Guid.CreateVersion7()]));

        RefundPayment refund = context.Outbox.Of<RefundPayment>().ShouldHaveSingleItem();
        refund.PaymentId.ShouldBe(paymentId);
        context.Outbox.Of<ReleaseInventory>().ShouldBeEmpty();

        // Still unwinding: the saga waits for the refund to be confirmed before it calls the order done.
        (await context.Saga.Exists(orderId, machine => machine.Compensating)).ShouldNotBeNull();

        await context.DeliverAsync(new PaymentRefunded(orderId, SagaVariant.Orchestrated, paymentId));
        (await context.Saga.Exists(orderId, machine => machine.Cancelled)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_failed_shipment_undoes_both_committed_steps()
    {
        // The interesting one. Two steps have committed in two different services, and both have to come
        // back, using the handles the saga kept precisely so that it could ask.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();
        Guid paymentId = Guid.CreateVersion7();
        Guid reservationId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId);
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 100m));
        await context.DeliverAsync(new InventoryReserved(orderId, SagaVariant.Orchestrated, reservationId));
        await context.DeliverAsync(
            new ShipmentFailed(orderId, SagaVariant.Orchestrated, "No carrier capacity."));

        context.Outbox.Of<ReleaseInventory>().ShouldHaveSingleItem().ReservationId.ShouldBe(reservationId);
        context.Outbox.Of<RefundPayment>().ShouldHaveSingleItem().PaymentId.ShouldBe(paymentId);

        // Nothing was booked with the carrier, so nothing is cancelled with the carrier.
        context.Outbox.Of<CancelShipment>().ShouldBeEmpty();

        (await context.Saga.Exists(orderId, machine => machine.Compensating)).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_order_is_only_cancelled_once_every_compensation_has_confirmed()
    {
        // Declaring the order cancelled after the first confirmation would report an unwound order while
        // one of the two services was still holding something.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();
        Guid paymentId = Guid.CreateVersion7();
        Guid reservationId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId);
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 100m));
        await context.DeliverAsync(new InventoryReserved(orderId, SagaVariant.Orchestrated, reservationId));
        await context.DeliverAsync(new ShipmentFailed(orderId, SagaVariant.Orchestrated, "No capacity."));

        await context.DeliverAsync(new PaymentRefunded(orderId, SagaVariant.Orchestrated, paymentId));
        (await context.Saga.Exists(orderId, machine => machine.Compensating, TimeSpan.FromMilliseconds(300)))
            .ShouldNotBeNull();

        await context.DeliverAsync(new InventoryReleased(orderId, SagaVariant.Orchestrated, reservationId));
        (await context.Saga.Exists(orderId, machine => machine.Cancelled)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_operator_can_unwind_a_completed_order()
    {
        // The only path that cancels a shipment, because nothing in the forward flow runs after shipping.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();
        Guid paymentId = Guid.CreateVersion7();
        Guid reservationId = Guid.CreateVersion7();
        Guid shipmentId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId);
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 100m));
        await context.DeliverAsync(new InventoryReserved(orderId, SagaVariant.Orchestrated, reservationId));
        await context.DeliverAsync(new ShipmentScheduled(orderId, SagaVariant.Orchestrated, shipmentId));

        await context.DeliverAsync(
            new CancelOrderRequested(orderId, SagaVariant.Orchestrated, "Customer changed their mind."));

        context.Outbox.Of<CancelShipment>().ShouldHaveSingleItem().ShipmentId.ShouldBe(shipmentId);
        context.Outbox.Of<ReleaseInventory>().ShouldHaveSingleItem();
        context.Outbox.Of<RefundPayment>().ShouldHaveSingleItem();

        await context.DeliverAsync(new ShipmentCancelled(orderId, SagaVariant.Orchestrated, shipmentId));
        await context.DeliverAsync(new InventoryReleased(orderId, SagaVariant.Orchestrated, reservationId));
        await context.DeliverAsync(new PaymentRefunded(orderId, SagaVariant.Orchestrated, paymentId));

        (await context.Saga.Exists(orderId, machine => machine.Cancelled)).ShouldNotBeNull();
    }

    [Fact]
    public async Task The_orchestrator_never_starts_a_saga_for_a_choreographed_order()
    {
        // Both strategies publish OrderCreated on the same broker, and the orchestrator does not subscribe
        // to it. An earlier version did, filtering by variant inside the state machine, and the saga
        // repository created an empty instance for every choreographed order before the filter ran.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();

        await context.Harness.Bus.Publish(new OrderCreated(
            orderId,
            SagaVariant.Choreographed,
            Guid.CreateVersion7(),
            50m,
            [new OrderLine(Guid.CreateVersion7(), 1, 50m)],
            DateTimeOffset.UtcNow));

        await context.Harness.Published.Any<OrderCreated>();

        (await context.Saga.Exists(orderId, machine => machine.AwaitingPayment, TimeSpan.FromMilliseconds(500)))
            .ShouldBeNull();

        context.Outbox.Staged.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_redelivered_event_does_not_move_a_finished_order()
    {
        // At-least-once delivery means the broker will repeat itself. An event arriving in a state that
        // has moved on is expected, and must be a no-op rather than a fault that pushes a healthy order
        // into the error queue.
        await using SagaTestContext context = await SagaTestContext.StartAsync();
        Guid orderId = Guid.CreateVersion7();
        Guid paymentId = Guid.CreateVersion7();

        await context.PlaceOrderAsync(orderId);
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 100m));
        await context.DeliverAsync(
            new InventoryReserved(orderId, SagaVariant.Orchestrated, Guid.CreateVersion7()));
        await context.DeliverAsync(
            new ShipmentScheduled(orderId, SagaVariant.Orchestrated, Guid.CreateVersion7()));

        (await context.Saga.Exists(orderId, machine => machine.Completed)).ShouldNotBeNull();

        int stagedBefore = context.Outbox.Staged.Count;
        await context.DeliverAsync(new PaymentAuthorized(orderId, SagaVariant.Orchestrated, paymentId, 100m));

        (await context.Saga.Exists(orderId, machine => machine.Completed)).ShouldNotBeNull();
        context.Outbox.Staged.Count.ShouldBe(stagedBefore);
    }
}
