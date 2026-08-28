using System.Text.Json;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

/// <summary>Where an order stands, from the customer's point of view.</summary>
public enum OrderStatus
{
    /// <summary>Accepted and committed. Nothing downstream has run yet.</summary>
    Created = 0,

    /// <summary>Funds are held.</summary>
    PaymentAuthorized = 1,

    /// <summary>Stock is held.</summary>
    InventoryReserved = 2,

    /// <summary>Everything worked.</summary>
    Completed = 3,

    /// <summary>Something failed and every completed step has been undone.</summary>
    Cancelled = 4,
}

/// <summary>An order as the customer sees it.</summary>
/// <remarks>
/// This row is a projection of what the participants reported, not the saga's own state. Keeping them
/// apart means the customer-facing status cannot drift from the timeline that explains it, and it is the
/// same code path in both coordination strategies.
/// </remarks>
public sealed class Order
{
    private Order()
    {
        LinesJson = null!;
    }

    /// <summary>Identifier. Also the correlation id on every message about this order.</summary>
    public Guid Id { get; private set; }

    /// <summary>Who placed it.</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>Order value.</summary>
    public decimal Total { get; private set; }

    /// <summary>What was ordered.</summary>
    public string LinesJson { get; private set; }

    /// <summary>Which coordination strategy is driving it.</summary>
    public SagaVariant Variant { get; private set; }

    /// <summary>Current status.</summary>
    public OrderStatus Status { get; private set; }

    /// <summary>Why it was cancelled, when it was.</summary>
    public string? CancellationReason { get; private set; }

    /// <summary>
    /// Set by the sweep when the order has been running too long without reaching a terminal state.
    /// </summary>
    /// <remarks>
    /// Deliberately a flag rather than a status. An order that is stuck is still in whatever state it
    /// reached, and losing that would throw away the only clue about where it stopped.
    /// </remarks>
    public bool IsStuck { get; private set; }

    /// <summary>When the order was accepted.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>When it reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>True once nothing further will happen to this order.</summary>
    public bool IsTerminal => Status is OrderStatus.Completed or OrderStatus.Cancelled;

    /// <summary>Accepts an order.</summary>
    /// <param name="customerId">Customer.</param>
    /// <param name="lines">What was ordered.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="now">Current time.</param>
    public static Order Place(
        Guid customerId,
        IReadOnlyList<OrderLine> lines,
        SagaVariant variant,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(lines));
        }

        return new Order
        {
            Id = Guid.CreateVersion7(now),
            CustomerId = customerId,
            Total = lines.Sum(line => line.UnitPrice * line.Quantity),
            LinesJson = JsonSerializer.Serialize(lines, MessageTypeRegistry.SerializerOptions),
            Variant = variant,
            Status = OrderStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Reads the order lines back.</summary>
    public IReadOnlyList<OrderLine> Lines() =>
        JsonSerializer.Deserialize<List<OrderLine>>(LinesJson, MessageTypeRegistry.SerializerOptions) ?? [];

    /// <summary>
    /// Moves the order forward, never backwards.
    /// </summary>
    /// <remarks>
    /// Messages arrive at least once and not necessarily in order, so a late redelivery of
    /// <c>PaymentAuthorized</c> can land after the order already completed. Refusing to go backwards is
    /// what keeps that from rewriting a finished order into an earlier state.
    /// </remarks>
    /// <param name="status">Reported status.</param>
    /// <param name="now">Current time.</param>
    public void Advance(OrderStatus status, DateTimeOffset now)
    {
        if (IsTerminal || status <= Status)
        {
            return;
        }

        Status = status;
        UpdatedAt = now;
        IsStuck = false;

        if (status is OrderStatus.Completed)
        {
            CompletedAt = now;
        }
    }

    /// <summary>Marks the order cancelled, with the reason the customer will be shown.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="now">Current time.</param>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status is OrderStatus.Cancelled)
        {
            return;
        }

        Status = OrderStatus.Cancelled;
        CancellationReason = reason;
        CompletedAt = now;
        UpdatedAt = now;
        IsStuck = false;
    }

    /// <summary>Flags the order as overdue.</summary>
    /// <param name="now">Current time.</param>
    public void FlagStuck(DateTimeOffset now)
    {
        if (IsTerminal || IsStuck)
        {
            return;
        }

        IsStuck = true;
        UpdatedAt = now;
    }
}

/// <summary>
/// One thing that happened to an order, from whichever service it happened in.
/// </summary>
/// <remarks>
/// Append-only, and the answer to "what actually happened to this order" without opening four services'
/// logs. It is written by the Order service alone, from the events every participant publishes anyway,
/// because database-per-service means no other service could write here even if it wanted to.
/// </remarks>
public sealed class OrderTimelineEntry
{
    private OrderTimelineEntry()
    {
        ServiceName = null!;
        EventType = null!;
        PayloadSnapshot = null!;
    }

    /// <summary>Identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Order.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>Which service reported it.</summary>
    public string ServiceName { get; private set; }

    /// <summary>What happened.</summary>
    public string EventType { get; private set; }

    /// <summary>The event, as it arrived.</summary>
    public string PayloadSnapshot { get; private set; }

    /// <summary>When the Order service recorded it.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Records an entry.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="serviceName">Reporting service.</param>
    /// <param name="eventType">Event name.</param>
    /// <param name="payloadSnapshot">Serialised event.</param>
    /// <param name="occurredAt">Current time.</param>
    public static OrderTimelineEntry Record(
        Guid orderId,
        string serviceName,
        string eventType,
        string payloadSnapshot,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        return new OrderTimelineEntry
        {
            Id = Guid.CreateVersion7(occurredAt),
            OrderId = orderId,
            ServiceName = serviceName,
            EventType = eventType,
            PayloadSnapshot = payloadSnapshot,
            OccurredAt = occurredAt,
        };
    }
}
