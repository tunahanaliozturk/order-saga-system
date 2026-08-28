using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.ShippingService;

/// <summary>Orchestration: the state machine told this service to book the shipment.</summary>
/// <param name="processor">Shipping work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class ScheduleShipmentConsumer(
    ShippingProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<ScheduleShipment>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "shipping.schedule.command";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Orchestrated;

    /// <inheritdoc />
    protected override Task HandleAsync(ScheduleShipment message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return processor.ScheduleAsync(
            message.CorrelationId,
            message.Lines.Sum(static line => line.Quantity),
            message.Variant);
    }
}

/// <summary>
/// Choreography: this service reacts to inventory being held, without being asked.
/// </summary>
/// <remarks>
/// Unlike Inventory, this service needs nothing from the original order beyond a count, so it does not
/// have to keep a copy of anything. That asymmetry is worth noticing: how much state choreography forces
/// a participant to hold depends entirely on what the upstream event happens to carry.
/// </remarks>
/// <param name="processor">Shipping work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class ScheduleOnInventoryReservedConsumer(
    ShippingProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<InventoryReserved>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "shipping.schedule.on-inventory-reserved";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override Task HandleAsync(InventoryReserved message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The count is not in the event, and this service has no legitimate way to ask for it. One
        // shipment per order is enough for the carrier booking, so it books one rather than inventing a
        // reason to reach into someone else's database.
        return processor.ScheduleAsync(message.CorrelationId, itemCount: 1, message.Variant);
    }
}

/// <summary>Orchestration: the state machine is unwinding and wants the booking cancelled.</summary>
/// <param name="processor">Shipping work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class CancelShipmentConsumer(
    ShippingProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<CancelShipment>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "shipping.cancel.command";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Orchestrated;

    /// <inheritdoc />
    protected override Task HandleAsync(CancelShipment message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Diagnostics.RecordCompensation(message.Variant, "shipping");
        return processor.CancelAsync(message.CorrelationId, message.Variant, cancellationToken);
    }
}
