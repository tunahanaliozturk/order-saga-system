using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.InventoryService;

/// <summary>Orchestration: the state machine told this service to hold stock.</summary>
/// <param name="processor">Inventory work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class ReserveInventoryConsumer(
    InventoryProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<ReserveInventory>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "inventory.reserve.command";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Orchestrated;

    /// <inheritdoc />
    protected override Task HandleAsync(ReserveInventory message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return processor.ReserveAsync(message.CorrelationId, message.Lines, message.Variant, cancellationToken);
    }
}

/// <summary>Choreography: this service reacts to payment succeeding, without being asked.</summary>
/// <param name="processor">Inventory work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class ReserveOnPaymentAuthorizedConsumer(
    InventoryProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<PaymentAuthorized>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "inventory.reserve.on-payment-authorized";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override async Task HandleAsync(PaymentAuthorized message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // PaymentAuthorized does not carry the order lines, and this service may not query the Order
        // service's database, so it reads what it remembered from OrderCreated.
        //
        // Those two messages race: nothing guarantees this service processed OrderCreated before Payment
        // published PaymentAuthorized. Throwing hands the problem to the transport, which redelivers a
        // few hundred milliseconds later, by which time the earlier message has landed. That race does not
        // exist in the orchestrated flow, because the state machine already holds the lines.
        IReadOnlyList<OrderLine> lines = await processor.RecalledLinesAsync(
            message.CorrelationId,
            cancellationToken);

        await processor.ReserveAsync(message.CorrelationId, lines, message.Variant, cancellationToken);
    }
}

/// <summary>Choreography: remembers order lines, because a later event will not carry them.</summary>
/// <param name="processor">Inventory work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class RememberOrderLinesConsumer(
    InventoryProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<OrderCreated>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "inventory.remember-order-lines";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override Task HandleAsync(OrderCreated message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        processor.RememberOrder(message.CorrelationId, message.Lines);
        return Task.CompletedTask;
    }
}

/// <summary>Orchestration: the state machine is unwinding and wants the stock back.</summary>
/// <param name="processor">Inventory work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class ReleaseInventoryConsumer(
    InventoryProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<ReleaseInventory>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "inventory.release.command";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Orchestrated;

    /// <inheritdoc />
    protected override Task HandleAsync(ReleaseInventory message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return processor.ReleaseAsync(message.CorrelationId, message.Variant, cancellationToken);
    }
}

/// <summary>Choreography: this service compensates itself on hearing that the shipment failed.</summary>
/// <param name="processor">Inventory work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class ReleaseOnShipmentFailedConsumer(
    InventoryProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<ShipmentFailed>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "inventory.release.on-shipment-failed";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override Task HandleAsync(ShipmentFailed message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Diagnostics.RecordCompensation(message.Variant, "inventory");
        return processor.ReleaseAsync(message.CorrelationId, message.Variant, cancellationToken);
    }
}
