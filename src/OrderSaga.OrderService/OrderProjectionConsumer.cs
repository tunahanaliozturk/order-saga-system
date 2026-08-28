using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

/// <summary>
/// Builds the customer-facing order status and the cross-service timeline from what participants report.
/// </summary>
/// <remarks>
/// <para>
/// One consumer for every event in the system, on one queue, in the one service that owns the order.
/// Database-per-service means Payment cannot write a timeline row into the Order service's database, so
/// the timeline is a projection rather than something four services append to.
/// </para>
/// <para>
/// It runs for both coordination strategies, unfiltered, which is what makes the two comparable: the
/// customer-visible status and the timeline are produced by identical code either way, so a difference
/// between the variants is a real difference in behaviour and not an artefact of two projections.
/// </para>
/// <para>
/// An order is marked cancelled as soon as a failure event arrives, before its compensations have
/// confirmed. That is the honest customer-facing answer, and it means the tests cannot lean on the status
/// to prove compensation happened: they check the Payment and Inventory services directly instead.
/// </para>
/// </remarks>
/// <param name="dbContext">Order database.</param>
/// <param name="guard">Idempotency guard.</param>
/// <param name="diagnostics">Metrics.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class OrderProjectionConsumer(
    OrderDbContext dbContext,
    IdempotencyGuard guard,
    SagaDiagnostics diagnostics,
    TimeProvider timeProvider) :
    IConsumer<OrderCreated>,
    IConsumer<PaymentAuthorized>,
    IConsumer<PaymentDeclined>,
    IConsumer<PaymentRefunded>,
    IConsumer<InventoryReserved>,
    IConsumer<InventoryUnavailable>,
    IConsumer<InventoryReleased>,
    IConsumer<ShipmentScheduled>,
    IConsumer<ShipmentFailed>,
    IConsumer<ShipmentCancelled>
{
    private const string ConsumerName = "order.projection";

    /// <inheritdoc />
    public Task Consume(ConsumeContext<OrderCreated> context) =>
        ApplyAsync(context, "order", static (_, _, _) => { });

    /// <inheritdoc />
    public Task Consume(ConsumeContext<PaymentAuthorized> context) =>
        ApplyAsync(context, "payment", static (order, _, now) =>
            order.Advance(OrderStatus.PaymentAuthorized, now));

    /// <inheritdoc />
    public Task Consume(ConsumeContext<PaymentDeclined> context) =>
        ApplyAsync(context, "payment", static (order, message, now) =>
            order.Cancel(message.Reason, now));

    /// <inheritdoc />
    public Task Consume(ConsumeContext<PaymentRefunded> context) =>
        ApplyAsync(context, "payment", static (_, _, _) => { });

    /// <inheritdoc />
    public Task Consume(ConsumeContext<InventoryReserved> context) =>
        ApplyAsync(context, "inventory", static (order, _, now) =>
            order.Advance(OrderStatus.InventoryReserved, now));

    /// <inheritdoc />
    public Task Consume(ConsumeContext<InventoryUnavailable> context) =>
        ApplyAsync(context, "inventory", static (order, message, now) =>
            order.Cancel($"Out of stock: {string.Join(", ", message.UnavailableSkus)}.", now));

    /// <inheritdoc />
    public Task Consume(ConsumeContext<InventoryReleased> context) =>
        ApplyAsync(context, "inventory", static (_, _, _) => { });

    /// <inheritdoc />
    public Task Consume(ConsumeContext<ShipmentScheduled> context) =>
        ApplyAsync(context, "shipping", static (order, _, now) =>
            order.Advance(OrderStatus.Completed, now));

    /// <inheritdoc />
    public Task Consume(ConsumeContext<ShipmentFailed> context) =>
        ApplyAsync(context, "shipping", static (order, message, now) =>
            order.Cancel(message.Reason, now));

    /// <inheritdoc />
    public Task Consume(ConsumeContext<ShipmentCancelled> context) =>
        ApplyAsync(context, "shipping", static (_, _, _) => { });

    private async Task ApplyAsync<TMessage>(
        ConsumeContext<TMessage> context,
        string serviceName,
        Action<Order, TMessage, DateTimeOffset> apply)
        where TMessage : class, ISagaMessage
    {
        ArgumentNullException.ThrowIfNull(context);

        Guid messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"{ConsumerName} received a {typeof(TMessage).Name} with no message id.");

        // The ledger name includes the message type. One projection consumer handles ten contracts, and
        // without this a PaymentAuthorized and an InventoryReserved that happened to share a message id
        // would be treated as the same message.
        string ledgerName = $"{ConsumerName}.{typeof(TMessage).Name}";

        ConsumeOutcome outcome = await guard.ExecuteOnceAsync(
            ledgerName,
            messageId,
            async token =>
            {
                DateTimeOffset now = timeProvider.GetUtcNow();
                TMessage message = context.Message;

                Order? order = await dbContext.Orders
                    .FirstOrDefaultAsync(entity => entity.Id == message.CorrelationId, token);

                if (order is null)
                {
                    // The order row is written in the same transaction as OrderCreated, so it exists
                    // before any of this can arrive. Reaching here means the event is about an order this
                    // service never wrote, which is worth recording rather than swallowing.
                    return;
                }

                bool wasTerminal = order.IsTerminal;
                apply(order, message, now);

                dbContext.Timeline.Add(OrderTimelineEntry.Record(
                    order.Id,
                    serviceName,
                    typeof(TMessage).Name,
                    MessageTypeRegistry.Serialize(message),
                    now));

                if (!wasTerminal && order.IsTerminal)
                {
                    diagnostics.RecordSagaCompleted(
                        order.Variant,
                        order.Status.ToString(),
                        now - order.CreatedAt);
                }
            },
            context.CancellationToken);

        if (outcome is ConsumeOutcome.Duplicate)
        {
            diagnostics.DuplicatesIgnored.Add(
                1,
                new KeyValuePair<string, object?>("messaging.consumer.name", ledgerName));
        }
    }
}

/// <summary>Maps the saga instance to explicit columns rather than a serialised blob.</summary>
public sealed class OrderSagaStateMap : SagaClassMap<OrderSagaState>
{
    /// <inheritdoc />
    protected override void Configure(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<OrderSagaState> entity,
        ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.ToTable("order_saga_state");

        entity.Property(state => state.CurrentState).HasMaxLength(64).IsRequired();
        entity.Property(state => state.LinesJson).HasColumnType("jsonb").IsRequired();
        entity.Property(state => state.Total).HasPrecision(18, 2);
        entity.Property(state => state.CancellationReason).HasMaxLength(512);

        // MassTransit increments this on every save and uses it as a concurrency token, so two events
        // racing on the same instance cannot silently overwrite each other.
        entity.Property(state => state.Version).IsConcurrencyToken();

        entity.Ignore(state => state.CompensationComplete);

        // The stuck-saga sweep scans for non-terminal instances older than the timeout.
        entity.HasIndex(state => new { state.CurrentState, state.StartedAt });
    }
}
