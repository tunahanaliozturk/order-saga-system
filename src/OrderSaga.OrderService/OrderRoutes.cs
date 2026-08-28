using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

/// <summary>Request body for placing an order.</summary>
/// <param name="CustomerId">Who is ordering.</param>
/// <param name="Lines">What they want.</param>
public sealed record PlaceOrderRequest(
    Guid CustomerId,
    [property: Required, MinLength(1)] IReadOnlyList<OrderLine> Lines);

/// <summary>Request body for the operator cancel endpoint.</summary>
/// <param name="Reason">Why.</param>
public sealed record CancelOrderRequest(string? Reason);

/// <summary>The order API. Two routes create orders, and which one decides how the order is coordinated.</summary>
public static class OrderRoutes
{
    /// <summary>Maps the order routes.</summary>
    /// <param name="routes">Route builder.</param>
    public static IEndpointRouteBuilder MapOrderRoutes(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        RouteGroupBuilder group = routes.MapGroup("/orders").WithTags("Orders");

        group.MapPost("/", (PlaceOrderRequest request, OrderOperations operations, CancellationToken token) =>
                operations.PlaceAsync(request, SagaVariant.Orchestrated, token))
            .WithName("PlaceOrchestratedOrder");

        group.MapPost("/choreographed", (PlaceOrderRequest request, OrderOperations operations, CancellationToken token) =>
                operations.PlaceAsync(request, SagaVariant.Choreographed, token))
            .WithName("PlaceChoreographedOrder");

        group.MapGet("/stuck", GetStuckAsync).WithName("ListStuckOrders");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetOrder");
        group.MapGet("/{id:guid}/timeline", GetTimelineAsync).WithName("GetOrderTimeline");
        group.MapPost("/{id:guid}/retry", RetryAsync).WithName("RetryOrder");
        group.MapPost("/{id:guid}/cancel", CancelAsync).WithName("CancelOrder");

        return routes;
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound>> GetAsync(
        Guid id,
        OrderDbContext dbContext,
        CancellationToken cancellationToken)
    {
        Order? order = await dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);

        return order is null ? TypedResults.NotFound() : TypedResults.Ok(OrderResponse.From(order));
    }

    private static async Task<Results<Ok<IReadOnlyList<TimelineResponse>>, NotFound>> GetTimelineAsync(
        Guid id,
        OrderDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.Orders.AnyAsync(entity => entity.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        List<TimelineResponse> entries = await dbContext.Timeline
            .AsNoTracking()
            .Where(entry => entry.OrderId == id)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Select(entry => new TimelineResponse(
                entry.ServiceName,
                entry.EventType,
                entry.PayloadSnapshot,
                entry.OccurredAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<TimelineResponse>>(entries);
    }

    private static async Task<Ok<IReadOnlyList<OrderResponse>>> GetStuckAsync(
        OrderDbContext dbContext,
        IOptions<StuckOrderOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Answers from both the flag and the clock. The flag is what the sweep set; the clock catches
        // anything that went overdue since the last sweep, so the endpoint never lags behind reality by
        // a sweep interval when someone is looking at it during an incident.
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - options.Value.Timeout;

        List<Order> stuck = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Status != OrderStatus.Completed
                && order.Status != OrderStatus.Cancelled
                && (order.IsStuck || order.CreatedAt < cutoff))
            .OrderBy(order => order.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<IReadOnlyList<OrderResponse>>([.. stuck.Select(OrderResponse.From)]);
    }

    private static async Task<Results<Accepted, NotFound, Conflict<string>>> RetryAsync(
        Guid id,
        OrderOperations operations,
        CancellationToken cancellationToken) =>
        await operations.RetryAsync(id, cancellationToken);

    private static async Task<Results<Accepted, NotFound, Conflict<string>>> CancelAsync(
        Guid id,
        CancelOrderRequest request,
        OrderOperations operations,
        CancellationToken cancellationToken) =>
        await operations.CancelAsync(id, request?.Reason, cancellationToken);
}

/// <summary>An order as the API returns it.</summary>
/// <param name="Id">Order id.</param>
/// <param name="CustomerId">Customer.</param>
/// <param name="Total">Order value.</param>
/// <param name="Status">Current status.</param>
/// <param name="SagaVariant">Which coordination strategy handled it.</param>
/// <param name="CancellationReason">Why it was cancelled, if it was.</param>
/// <param name="IsStuck">Whether it has been running too long without finishing.</param>
/// <param name="Lines">What was ordered.</param>
/// <param name="CreatedAt">When it was accepted.</param>
/// <param name="CompletedAt">When it finished.</param>
public sealed record OrderResponse(
    Guid Id,
    Guid CustomerId,
    decimal Total,
    string Status,
    string SagaVariant,
    string? CancellationReason,
    bool IsStuck,
    IReadOnlyList<OrderLine> Lines,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    /// <summary>Projects an entity.</summary>
    /// <param name="order">The order.</param>
    public static OrderResponse From(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return new OrderResponse(
            order.Id,
            order.CustomerId,
            order.Total,
            order.Status.ToString(),
            order.Variant.ToString(),
            order.CancellationReason,
            order.IsStuck,
            order.Lines(),
            order.CreatedAt,
            order.CompletedAt);
    }
}

/// <summary>One entry in an order's story.</summary>
/// <param name="ServiceName">Which service reported it.</param>
/// <param name="EventType">What happened.</param>
/// <param name="PayloadSnapshot">The event as it arrived.</param>
/// <param name="OccurredAt">When.</param>
public sealed record TimelineResponse(
    string ServiceName,
    string EventType,
    string PayloadSnapshot,
    DateTimeOffset OccurredAt);
