using MassTransit;
using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

/// <summary>
/// What the orchestrator remembers about one order in flight.
/// </summary>
/// <remarks>
/// <para>
/// Explicit typed columns rather than a serialised blob. A blob is quicker to set up and defers the cost:
/// the first time the state shape changes, in-flight instances fail to deserialise in production with no
/// warning. As columns, a shape change is a migration, and CI fails on a pending model change instead.
/// </para>
/// <para>
/// This state is the reason the orchestrator can be killed and resume. Nothing about the flow lives in
/// process memory.
/// </para>
/// </remarks>
public sealed class OrderSagaState : SagaStateMachineInstance, ISagaVersion
{
    /// <summary>The order id. One value correlates the saga, the messages, and the timeline.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>Current state name, as MassTransit stores it.</summary>
    public string CurrentState { get; set; } = null!;

    /// <summary>
    /// Optimistic concurrency token.
    /// </summary>
    /// <remarks>
    /// Two events for the same order can be consumed concurrently, for instance a refund confirmation and
    /// a release confirmation arriving together during compensation. Without this, one of the two writes
    /// is silently lost and the saga waits forever for a step that already finished.
    /// </remarks>
    public int Version { get; set; }

    /// <summary>Who placed the order.</summary>
    public Guid CustomerId { get; set; }

    /// <summary>Order value.</summary>
    public decimal Total { get; set; }

    /// <summary>What was ordered, kept so compensating steps can be re-driven without asking anyone.</summary>
    public string LinesJson { get; set; } = null!;

    /// <summary>Payment handle, once there is one.</summary>
    public Guid? PaymentId { get; set; }

    /// <summary>Reservation handle, once there is one.</summary>
    public Guid? ReservationId { get; set; }

    /// <summary>Shipment handle, once there is one.</summary>
    public Guid? ShipmentId { get; set; }

    /// <summary>Whether a refund has been asked for and not yet confirmed.</summary>
    public bool AwaitingRefund { get; set; }

    /// <summary>Whether a stock release has been asked for and not yet confirmed.</summary>
    public bool AwaitingRelease { get; set; }

    /// <summary>Whether a shipment cancellation has been asked for and not yet confirmed.</summary>
    public bool AwaitingShipmentCancellation { get; set; }

    /// <summary>Why the order is being unwound.</summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// When the saga started.
    /// </summary>
    /// <remarks>
    /// Stored once, at the start, and compared against a single clock. Deriving a saga's age from
    /// timestamps written by four different containers would make the stuck-order timeout a function of
    /// clock skew.
    /// </remarks>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the saga reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>True once every compensating step has been confirmed.</summary>
    public bool CompensationComplete =>
        !AwaitingRefund && !AwaitingRelease && !AwaitingShipmentCancellation;

    /// <summary>Reads the order lines back.</summary>
    public IReadOnlyList<OrderLine> Lines() =>
        System.Text.Json.JsonSerializer.Deserialize<List<OrderLine>>(
            LinesJson,
            BuildingBlocks.Messaging.MessageTypeRegistry.SerializerOptions) ?? [];
}
