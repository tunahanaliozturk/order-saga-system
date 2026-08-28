using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.PaymentService;

/// <summary>Orchestration: the state machine told this service to hold funds.</summary>
/// <param name="processor">Payment work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class AuthorizePaymentConsumer(
    PaymentProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<AuthorizePayment>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "payment.authorize.command";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Orchestrated;

    /// <inheritdoc />
    protected override Task HandleAsync(AuthorizePayment message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return processor.AuthorizeAsync(
            message.CorrelationId,
            message.CustomerId,
            message.Amount,
            message.Variant,
            cancellationToken);
    }
}

/// <summary>
/// Choreography: nobody told this service anything. It heard an order was created and acted.
/// </summary>
/// <remarks>
/// This is the whole difference between the two strategies in one class. There is no orchestrator to ask,
/// and this service does not know or care what runs after it.
/// </remarks>
/// <param name="processor">Payment work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class AuthorizeOnOrderCreatedConsumer(
    PaymentProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<OrderCreated>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "payment.authorize.on-order-created";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override Task HandleAsync(OrderCreated message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        return processor.AuthorizeAsync(
            message.CorrelationId,
            message.CustomerId,
            message.Total,
            message.Variant,
            cancellationToken);
    }
}

/// <summary>Orchestration: the state machine is unwinding and wants the hold released.</summary>
/// <param name="processor">Payment work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class RefundPaymentConsumer(
    PaymentProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<RefundPayment>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "payment.refund.command";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Orchestrated;

    /// <inheritdoc />
    protected override Task HandleAsync(RefundPayment message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return processor.RefundAsync(message.CorrelationId, message.Variant, cancellationToken);
    }
}

/// <summary>
/// Choreography: this service compensates itself on hearing that inventory could not be met.
/// </summary>
/// <remarks>
/// Restricted to the choreographed variant on purpose. Without that restriction it would also fire for
/// orchestrated orders, whose refunds the state machine is already commanding, and the customer would be
/// refunded twice by two mechanisms that each believe they are the only one running.
/// </remarks>
/// <param name="processor">Payment work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class RefundOnInventoryUnavailableConsumer(
    PaymentProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<InventoryUnavailable>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "payment.refund.on-inventory-unavailable";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override Task HandleAsync(InventoryUnavailable message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Diagnostics.RecordCompensation(message.Variant, "payment");
        return processor.RefundAsync(message.CorrelationId, message.Variant, cancellationToken);
    }
}

/// <summary>Choreography: this service compensates itself on hearing that the shipment failed.</summary>
/// <param name="processor">Payment work.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
public sealed class RefundOnShipmentFailedConsumer(
    PaymentProcessor processor,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics) : IdempotentConsumer<ShipmentFailed>(guard, diagnostics)
{
    /// <inheritdoc />
    protected override string ConsumerName => "payment.refund.on-shipment-failed";

    /// <inheritdoc />
    protected override SagaVariant? RestrictedTo => SagaVariant.Choreographed;

    /// <inheritdoc />
    protected override Task HandleAsync(ShipmentFailed message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        Diagnostics.RecordCompensation(message.Variant, "payment");
        return processor.RefundAsync(message.CorrelationId, message.Variant, cancellationToken);
    }
}
