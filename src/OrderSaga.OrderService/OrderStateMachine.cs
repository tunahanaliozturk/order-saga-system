using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.OrderService;

/// <summary>
/// The orchestrated saga: one place that knows the whole order flow.
/// </summary>
/// <remarks>
/// <para>
/// Every outbound command is staged in the outbox rather than sent directly. The saga repository saves
/// the instance and the outbox rows in the same transaction, so the orchestrator cannot advance its own
/// state and then fail to tell anyone. That is the same dual-write problem the participants have, and it
/// applies just as much to the component coordinating them.
/// </para>
/// <para>
/// This is the readable half of the comparison with choreography. The whole flow, including which steps
/// have to be undone from where, is in one file. The cost is that this file knows about all three
/// downstream services, which is exactly the coupling choreography avoids.
/// </para>
/// </remarks>
public sealed class OrderStateMachine : MassTransitStateMachine<OrderSagaState>
{
    /// <summary>Builds the machine.</summary>
    public OrderStateMachine()
    {
        InstanceState(instance => instance.CurrentState);

        Event(() => SagaStarted, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => PaymentApproved, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => PaymentRejected, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => StockReserved, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => StockUnavailable, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => ShipmentBooked, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => ShipmentRejected, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => RefundConfirmed, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => ReleaseConfirmed, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => ShipmentCancellationConfirmed, config => config.CorrelateById(context => context.Message.CorrelationId));
        Event(() => CancellationRequested, config => config.CorrelateById(context => context.Message.CorrelationId));

        Initially(
            // Only orchestrated orders produce this message, so no filter is needed and no saga instance
            // is ever created for an order that choreography is running.
            When(SagaStarted)
                .Then(context =>
                {
                    context.Saga.CustomerId = context.Message.CustomerId;
                    context.Saga.Total = context.Message.Total;
                    context.Saga.LinesJson = System.Text.Json.JsonSerializer.Serialize(
                        context.Message.Lines,
                        MessageTypeRegistry.SerializerOptions);
                    context.Saga.StartedAt = context.Message.OccurredAt;
                })
                .Then(context => Stage(context, new AuthorizePayment(
                    context.Saga.CorrelationId,
                    SagaVariant.Orchestrated,
                    context.Saga.CustomerId,
                    context.Saga.Total)))
                .TransitionTo(AwaitingPayment));

        During(
            AwaitingPayment,
            When(PaymentApproved)
                .Then(context => context.Saga.PaymentId = context.Message.PaymentId)
                .Then(context => Stage(context, new ReserveInventory(
                    context.Saga.CorrelationId,
                    SagaVariant.Orchestrated,
                    context.Saga.Lines())))
                .TransitionTo(AwaitingInventory),

            // Nothing has committed downstream yet, so there is nothing to undo. This is the one failure
            // path in the flow that needs no compensation at all.
            When(PaymentRejected)
                .Then(context => context.Saga.CancellationReason = context.Message.Reason)
                .ThenAsync(context => FinishAsync(context, cancelled: true))
                .TransitionTo(Cancelled));

        During(
            AwaitingInventory,
            When(StockReserved)
                .Then(context => context.Saga.ReservationId = context.Message.ReservationId)
                .Then(context => Stage(context, new ScheduleShipment(
                    context.Saga.CorrelationId,
                    SagaVariant.Orchestrated,
                    context.Saga.Lines())))
                .TransitionTo(AwaitingShipment),

            // One step has committed: the payment. So exactly one step is undone.
            When(StockUnavailable)
                .Then(context =>
                {
                    context.Saga.CancellationReason =
                        $"Out of stock: {string.Join(", ", context.Message.UnavailableSkus)}.";
                })
                .Then(context => RequestRefund(context))
                .TransitionTo(Compensating));

        During(
            AwaitingShipment,
            When(ShipmentBooked)
                .Then(context => context.Saga.ShipmentId = context.Message.ShipmentId)
                .ThenAsync(context => FinishAsync(context, cancelled: false))
                .TransitionTo(Completed),

            // Two steps have committed. Both are undone, and the compensating commands go out together
            // because they touch different services and nothing orders them relative to each other.
            When(ShipmentRejected)
                .Then(context => context.Saga.CancellationReason = context.Message.Reason)
                .Then(context => RequestRelease(context))
                .Then(context => RequestRefund(context))
                .TransitionTo(Compensating));

        // An operator unwinding an order that already finished. This is the only path that cancels a
        // shipment, because nothing in the forward flow runs after shipping.
        During(
            Completed,
            When(CancellationRequested)
                .Then(context => context.Saga.CancellationReason = context.Message.Reason)
                .Then(context => RequestShipmentCancellation(context))
                .Then(context => RequestRelease(context))
                .Then(context => RequestRefund(context))
                .TransitionTo(Compensating));

        During(
            Compensating,
            When(RefundConfirmed)
                .Then(context => context.Saga.AwaitingRefund = false)
                .ThenAsync(context => TryFinishCompensationAsync(context)),

            When(ReleaseConfirmed)
                .Then(context => context.Saga.AwaitingRelease = false)
                .ThenAsync(context => TryFinishCompensationAsync(context)),

            When(ShipmentCancellationConfirmed)
                .Then(context => context.Saga.AwaitingShipmentCancellation = false)
                .ThenAsync(context => TryFinishCompensationAsync(context)));

        // Redeliveries arrive after the saga has moved on. Ignoring them is correct and deliberate:
        // treating an out-of-state event as a fault would push a perfectly healthy order into the error
        // queue every time the broker did what it promises to do.
        During(
            Completed,
            Ignore(SagaStarted),
            Ignore(PaymentApproved),
            Ignore(StockReserved),
            Ignore(ShipmentBooked));

        During(
            Cancelled,
            Ignore(SagaStarted),
            Ignore(PaymentApproved),
            Ignore(PaymentRejected),
            Ignore(StockReserved),
            Ignore(StockUnavailable),
            Ignore(ShipmentBooked),
            Ignore(ShipmentRejected),
            Ignore(RefundConfirmed),
            Ignore(ReleaseConfirmed),
            Ignore(ShipmentCancellationConfirmed));
    }

    /// <summary>Waiting for the payment service.</summary>
    public State AwaitingPayment { get; private set; } = null!;

    /// <summary>Waiting for the inventory service.</summary>
    public State AwaitingInventory { get; private set; } = null!;

    /// <summary>Waiting for the shipping service.</summary>
    public State AwaitingShipment { get; private set; } = null!;

    /// <summary>Unwinding the steps that already committed.</summary>
    public State Compensating { get; private set; } = null!;

    /// <summary>Everything worked.</summary>
    public State Completed { get; private set; } = null!;

    /// <summary>Something failed and everything committed has been undone.</summary>
    public State Cancelled { get; private set; } = null!;

    /// <summary>An orchestrated order was accepted.</summary>
    public Event<StartOrderSaga> SagaStarted { get; private set; } = null!;

    /// <summary>Funds are held.</summary>
    public Event<PaymentAuthorized> PaymentApproved { get; private set; } = null!;

    /// <summary>Funds were refused.</summary>
    public Event<PaymentDeclined> PaymentRejected { get; private set; } = null!;

    /// <summary>Stock is held.</summary>
    public Event<InventoryReserved> StockReserved { get; private set; } = null!;

    /// <summary>Stock could not be held.</summary>
    public Event<InventoryUnavailable> StockUnavailable { get; private set; } = null!;

    /// <summary>The shipment is booked.</summary>
    public Event<ShipmentScheduled> ShipmentBooked { get; private set; } = null!;

    /// <summary>The shipment could not be booked.</summary>
    public Event<ShipmentFailed> ShipmentRejected { get; private set; } = null!;

    /// <summary>A refund completed.</summary>
    public Event<PaymentRefunded> RefundConfirmed { get; private set; } = null!;

    /// <summary>A stock release completed.</summary>
    public Event<InventoryReleased> ReleaseConfirmed { get; private set; } = null!;

    /// <summary>A shipment cancellation completed.</summary>
    public Event<ShipmentCancelled> ShipmentCancellationConfirmed { get; private set; } = null!;

    /// <summary>An operator asked for a completed order to be unwound.</summary>
    public Event<CancelOrderRequested> CancellationRequested { get; private set; } = null!;

    private static void Stage<TEvent, TMessage>(BehaviorContext<OrderSagaState, TEvent> context, TMessage message)
        where TEvent : class
        where TMessage : class, ISagaMessage
    {
        // Resolved from the consume scope, which is the same scope the saga repository loaded the
        // instance in, so the outbox row and the saga row are saved together.
        context.GetPayload<IServiceProvider>()
            .GetRequiredService<IOutboxWriter>()
            .Stage(message);
    }

    private static void RequestRefund<TEvent>(BehaviorContext<OrderSagaState, TEvent> context)
        where TEvent : class
    {
        if (context.Saga.PaymentId is not { } paymentId)
        {
            return;
        }

        context.Saga.AwaitingRefund = true;
        Stage(context, new RefundPayment(context.Saga.CorrelationId, SagaVariant.Orchestrated, paymentId));
        Diagnostics(context).RecordCompensation(SagaVariant.Orchestrated, "payment");
    }

    private static void RequestRelease<TEvent>(BehaviorContext<OrderSagaState, TEvent> context)
        where TEvent : class
    {
        if (context.Saga.ReservationId is not { } reservationId)
        {
            return;
        }

        context.Saga.AwaitingRelease = true;
        Stage(context, new ReleaseInventory(context.Saga.CorrelationId, SagaVariant.Orchestrated, reservationId));
        Diagnostics(context).RecordCompensation(SagaVariant.Orchestrated, "inventory");
    }

    private static void RequestShipmentCancellation<TEvent>(BehaviorContext<OrderSagaState, TEvent> context)
        where TEvent : class
    {
        if (context.Saga.ShipmentId is not { } shipmentId)
        {
            return;
        }

        context.Saga.AwaitingShipmentCancellation = true;
        Stage(context, new CancelShipment(context.Saga.CorrelationId, SagaVariant.Orchestrated, shipmentId));
        Diagnostics(context).RecordCompensation(SagaVariant.Orchestrated, "shipping");
    }

    private static async Task TryFinishCompensationAsync<TEvent>(BehaviorContext<OrderSagaState, TEvent> context)
        where TEvent : class
    {
        if (!context.Saga.CompensationComplete)
        {
            return;
        }

        await FinishAsync(context, cancelled: true);
        await context.TransitionToState(((OrderStateMachine)context.StateMachine).Cancelled);
    }

    private static Task FinishAsync<TEvent>(BehaviorContext<OrderSagaState, TEvent> context, bool cancelled)
        where TEvent : class
    {
        IServiceProvider provider = context.GetPayload<IServiceProvider>();
        TimeProvider clock = provider.GetRequiredService<TimeProvider>();

        DateTimeOffset now = clock.GetUtcNow();
        context.Saga.CompletedAt = now;

        provider.GetRequiredService<SagaDiagnostics>().RecordSagaCompleted(
            SagaVariant.Orchestrated,
            cancelled ? nameof(Cancelled) : nameof(Completed),
            now - context.Saga.StartedAt);

        return Task.CompletedTask;
    }

    private static SagaDiagnostics Diagnostics<TEvent>(BehaviorContext<OrderSagaState, TEvent> context)
        where TEvent : class =>
        context.GetPayload<IServiceProvider>().GetRequiredService<SagaDiagnostics>();
}
