namespace OrderSaga.Contracts;

/// <summary>Which coordination strategy is driving an order.</summary>
/// <remarks>
/// Both strategies run on the same four services against the same broker, so every message says which one
/// it belongs to. Without it, a choreography consumer subscribed to <see cref="ShipmentFailed"/> would also
/// self-compensate orders the orchestrator is already compensating, and the order would be refunded twice.
/// </remarks>
public enum SagaVariant
{
    /// <summary>A central state machine decides the next step. Participants know nothing about each other.</summary>
    Orchestrated = 0,

    /// <summary>Every participant reacts to the previous one's event. There is no coordinator.</summary>
    Choreographed = 1,
}

/// <summary>One line of an order.</summary>
/// <param name="Sku">Stock keeping unit.</param>
/// <param name="Quantity">How many.</param>
/// <param name="UnitPrice">Price per unit.</param>
public sealed record OrderLine(Guid Sku, int Quantity, decimal UnitPrice);

/// <summary>Common shape of everything on the bus.</summary>
/// <remarks>
/// <para>
/// <c>CorrelationId</c> is the order id, not a separate value. One identifier threads the whole business
/// transaction: it correlates the saga instance, keys the timeline, and lands in every log line, so
/// reconstructing what happened never needs a lookup table.
/// </para>
/// <para>
/// These records deliberately have no package references. Contracts are the one thing every service and
/// every external consumer has to agree on, so they must not drag a broker client or an ORM along.
/// </para>
/// </remarks>
public interface ISagaMessage
{
    /// <summary>The order this message is about. Also the saga correlation id.</summary>
    Guid CorrelationId { get; }

    /// <summary>Which coordination strategy owns this order.</summary>
    SagaVariant Variant { get; }
}

// ---------------------------------------------------------------------------------------------------
// Order
// ---------------------------------------------------------------------------------------------------

/// <summary>An order was accepted. The starting gun for the choreographed flow.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="CustomerId">Who placed it.</param>
/// <param name="Total">Amount to authorize.</param>
/// <param name="Lines">What to reserve.</param>
/// <param name="OccurredAt">When the order row was committed.</param>
public sealed record OrderCreated(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid CustomerId,
    decimal Total,
    IReadOnlyList<OrderLine> Lines,
    DateTimeOffset OccurredAt) : ISagaMessage;

/// <summary>
/// Starts the orchestrated saga. Only orchestrated orders produce one.
/// </summary>
/// <remarks>
/// A separate message rather than a filter on <see cref="OrderCreated"/>. Filtering inside the state
/// machine still lets the saga repository create an instance before the filter is evaluated, so every
/// choreographed order left an empty saga row behind and the insert failed on a not-null column. Giving
/// the orchestrator its own trigger means it never sees an order that is not its to run.
/// </remarks>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy. Always orchestrated.</param>
/// <param name="CustomerId">Who placed it.</param>
/// <param name="Total">Amount to authorize.</param>
/// <param name="Lines">What to reserve.</param>
/// <param name="OccurredAt">When the order row was committed.</param>
public sealed record StartOrderSaga(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid CustomerId,
    decimal Total,
    IReadOnlyList<OrderLine> Lines,
    DateTimeOffset OccurredAt) : ISagaMessage;

/// <summary>
/// An operator asked for a finished order to be unwound.
/// </summary>
/// <remarks>
/// Lives here with every other contract, not in the Order service, because the relay resolves a stored
/// message name against this assembly alone. A contract declared anywhere else is staged happily and then
/// never published, which is a quiet failure rather than a loud one.
/// </remarks>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="Reason">Why.</param>
public sealed record CancelOrderRequested(
    Guid CorrelationId,
    SagaVariant Variant,
    string Reason) : ISagaMessage;

// ---------------------------------------------------------------------------------------------------
// Payment
// ---------------------------------------------------------------------------------------------------

/// <summary>Orchestrator command: hold funds for this order.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="CustomerId">Who to charge.</param>
/// <param name="Amount">How much.</param>
public sealed record AuthorizePayment(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid CustomerId,
    decimal Amount) : ISagaMessage;

/// <summary>Funds are held.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="PaymentId">Handle the refund compensation needs.</param>
/// <param name="Amount">How much was held.</param>
public sealed record PaymentAuthorized(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid PaymentId,
    decimal Amount) : ISagaMessage;

/// <summary>Funds were not held. Nothing to compensate, because nothing downstream has run yet.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="Reason">What to tell the customer.</param>
public sealed record PaymentDeclined(
    Guid CorrelationId,
    SagaVariant Variant,
    string Reason) : ISagaMessage;

/// <summary>Compensation: give the held funds back.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="PaymentId">Which authorization to reverse.</param>
public sealed record RefundPayment(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid PaymentId) : ISagaMessage;

/// <summary>The refund went through.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="PaymentId">Which authorization was reversed.</param>
public sealed record PaymentRefunded(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid PaymentId) : ISagaMessage;

// ---------------------------------------------------------------------------------------------------
// Inventory
// ---------------------------------------------------------------------------------------------------

/// <summary>Orchestrator command: hold stock for this order.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="Lines">What to hold.</param>
public sealed record ReserveInventory(
    Guid CorrelationId,
    SagaVariant Variant,
    IReadOnlyList<OrderLine> Lines) : ISagaMessage;

/// <summary>Stock is held.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ReservationId">Handle the release compensation needs.</param>
public sealed record InventoryReserved(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid ReservationId) : ISagaMessage;

/// <summary>Stock is not available. Payment has already been authorized and needs reversing.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="UnavailableSkus">Which lines could not be met.</param>
public sealed record InventoryUnavailable(
    Guid CorrelationId,
    SagaVariant Variant,
    IReadOnlyList<Guid> UnavailableSkus) : ISagaMessage;

/// <summary>Compensation: put the stock back.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ReservationId">Which reservation to release.</param>
public sealed record ReleaseInventory(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid ReservationId) : ISagaMessage;

/// <summary>The stock is back.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ReservationId">Which reservation was released.</param>
public sealed record InventoryReleased(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid ReservationId) : ISagaMessage;

// ---------------------------------------------------------------------------------------------------
// Shipping
// ---------------------------------------------------------------------------------------------------

/// <summary>Orchestrator command: book the shipment.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="Lines">What is going out.</param>
public sealed record ScheduleShipment(
    Guid CorrelationId,
    SagaVariant Variant,
    IReadOnlyList<OrderLine> Lines) : ISagaMessage;

/// <summary>The shipment is booked. The order is done.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ShipmentId">Handle the cancel compensation needs.</param>
public sealed record ShipmentScheduled(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid ShipmentId) : ISagaMessage;

/// <summary>The shipment could not be booked. Both earlier steps need reversing.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="Reason">What went wrong.</param>
public sealed record ShipmentFailed(
    Guid CorrelationId,
    SagaVariant Variant,
    string Reason) : ISagaMessage;

/// <summary>Compensation: unbook the shipment.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ShipmentId">Which shipment to cancel.</param>
public sealed record CancelShipment(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid ShipmentId) : ISagaMessage;

/// <summary>The shipment is unbooked.</summary>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ShipmentId">Which shipment was cancelled.</param>
public sealed record ShipmentCancelled(
    Guid CorrelationId,
    SagaVariant Variant,
    Guid ShipmentId) : ISagaMessage;

// ---------------------------------------------------------------------------------------------------
// Timeline
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// One entry in the cross-service story of an order.
/// </summary>
/// <remarks>
/// Database-per-service means Payment cannot write a row into the Order service's database, so the
/// timeline is a projection: every service publishes what it did, and the Order service is the only thing
/// that consumes these and appends to <c>order_timeline</c>. That keeps the audit trail in one queryable
/// place without any service reaching into another's schema.
/// </remarks>
/// <param name="CorrelationId">Order id.</param>
/// <param name="Variant">Coordination strategy.</param>
/// <param name="ServiceName">Who recorded it.</param>
/// <param name="EventType">What happened.</param>
/// <param name="PayloadSnapshot">Serialised detail, for post-mortems.</param>
/// <param name="OccurredAt">When.</param>
public sealed record OrderTimelineEntryRecorded(
    Guid CorrelationId,
    SagaVariant Variant,
    string ServiceName,
    string EventType,
    string PayloadSnapshot,
    DateTimeOffset OccurredAt) : ISagaMessage;
