using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

/// <summary>
/// The write side of the order API.
/// </summary>
/// <remarks>
/// Placing an order is one local transaction: the order row and the <c>OrderCreated</c> outbox row commit
/// together or not at all. Nothing is published from inside the request. That is what makes "the customer
/// got a 202 but nothing ever happened" impossible rather than unlikely.
/// </remarks>
/// <param name="dbContext">Order database.</param>
/// <param name="outbox">Stages outbound messages in the caller's transaction.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class OrderOperations(
    OrderDbContext dbContext,
    IOutboxWriter outbox,
    TimeProvider timeProvider)
{
    /// <summary>Accepts an order and starts whichever coordination strategy the route chose.</summary>
    /// <param name="request">Order details.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Results<Accepted<OrderResponse>, ValidationProblem>> PlaceAsync(
        PlaceOrderRequest request,
        SagaVariant variant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [nameof(request.Lines)] = ["An order needs at least one line."],
            });
        }

        if (request.Lines.Any(static line => line.Quantity <= 0 || line.UnitPrice <= 0))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [nameof(request.Lines)] = ["Every line needs a positive quantity and unit price."],
            });
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        Order order = Order.Place(request.CustomerId, request.Lines, variant, now);

        dbContext.Orders.Add(order);

        // Announced either way: the timeline projection needs it, and in choreography it is what the
        // Payment service reacts to.
        outbox.Stage(new OrderCreated(
            order.Id,
            variant,
            order.CustomerId,
            order.Total,
            request.Lines,
            now));

        if (variant is SagaVariant.Orchestrated)
        {
            // The orchestrator gets its own trigger, so it never has to decide whether an order is its
            // to run.
            outbox.Stage(new StartOrderSaga(
                order.Id,
                variant,
                order.CustomerId,
                order.Total,
                request.Lines,
                now));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return TypedResults.Accepted($"/orders/{order.Id}", OrderResponse.From(order));
    }

    /// <summary>
    /// Re-drives an order that stopped making progress.
    /// </summary>
    /// <remarks>
    /// Re-publishing the event that starts the current step is safe precisely because every consumer is
    /// idempotent: a step that actually completed absorbs the repeat, and one that never ran gets its
    /// chance. Note that this is a nudge, not a restart. Nothing re-runs the saga from the beginning,
    /// which would turn a real bug into a loop that looks healthy.
    /// </remarks>
    /// <param name="orderId">Order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Results<Accepted, NotFound, Conflict<string>>> RetryAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        Order? order = await dbContext.Orders
            .FirstOrDefaultAsync(entity => entity.Id == orderId, cancellationToken);

        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (order.IsTerminal)
        {
            return TypedResults.Conflict($"Order {orderId} is already {order.Status}.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Choreography has no orchestrator to re-issue a command, so what gets re-driven is the event the
        // participants react to. Orchestration re-issues the command for the step the order is waiting on.
        ISagaMessage message = (order.Status, order.Variant) switch
        {
            (OrderStatus.Created, SagaVariant.Orchestrated) =>
                new StartOrderSaga(order.Id, order.Variant, order.CustomerId, order.Total, order.Lines(), now),

            (OrderStatus.Created, SagaVariant.Choreographed) =>
                new OrderCreated(order.Id, order.Variant, order.CustomerId, order.Total, order.Lines(), now),

            (OrderStatus.PaymentAuthorized, SagaVariant.Orchestrated) =>
                new ReserveInventory(order.Id, order.Variant, order.Lines()),

            (OrderStatus.InventoryReserved, SagaVariant.Orchestrated) =>
                new ScheduleShipment(order.Id, order.Variant, order.Lines()),

            // Re-driving the choreographed flow past its first step means replaying the event the next
            // participant subscribes to. The payment id is not known here, and does not need to be: the
            // consumer that acts on it looks up its own record by order id.
            (OrderStatus.PaymentAuthorized, SagaVariant.Choreographed) =>
                new PaymentAuthorized(order.Id, order.Variant, Guid.Empty, order.Total),

            (OrderStatus.InventoryReserved, SagaVariant.Choreographed) =>
                new InventoryReserved(order.Id, order.Variant, Guid.Empty),

            _ => throw new InvalidOperationException($"No retry is defined for status {order.Status}."),
        };

        outbox.Stage(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        return TypedResults.Accepted($"/orders/{orderId}");
    }

    /// <summary>Asks the orchestrator to unwind a completed order.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Results<Accepted, NotFound, Conflict<string>>> CancelAsync(
        Guid orderId,
        string? reason,
        CancellationToken cancellationToken)
    {
        Order? order = await dbContext.Orders
            .FirstOrDefaultAsync(entity => entity.Id == orderId, cancellationToken);

        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (order.Variant is not SagaVariant.Orchestrated)
        {
            return TypedResults.Conflict(
                "Unwinding a finished order needs a coordinator, and the choreographed flow does not have one.");
        }

        if (order.Status is not OrderStatus.Completed)
        {
            return TypedResults.Conflict($"Only a completed order can be unwound. This one is {order.Status}.");
        }

        outbox.Stage(new CancelOrderRequested(
            order.Id,
            order.Variant,
            string.IsNullOrWhiteSpace(reason) ? "Cancelled by an operator." : reason));

        await dbContext.SaveChangesAsync(cancellationToken);

        return TypedResults.Accepted($"/orders/{orderId}");
    }
}
