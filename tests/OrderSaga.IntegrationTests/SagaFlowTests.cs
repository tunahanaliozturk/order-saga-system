using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.Contracts;
using OrderSaga.InventoryService;
using OrderSaga.PaymentService;
using OrderSaga.ShippingService;
using Shouldly;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// The order flow and every compensating path, run against both coordination strategies.
/// </summary>
/// <remarks>
/// Assertions are made against the participants' own databases rather than against the order status. The
/// order is marked cancelled as soon as a failure event arrives, so leaning on the status would let a
/// broken compensation pass: what proves a refund happened is a refunded row in the Payment service.
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class SagaFlowTests(ContainerFixture containers)
{
    [Theory]
    [InlineData(SagaVariant.Orchestrated)]
    [InlineData(SagaVariant.Choreographed)]
    public async Task An_order_completes_and_every_participant_did_its_part(SagaVariant variant)
    {
        await using SagaSystem system = await SagaSystem.StartAsync(containers, $"happy_{variant}");

        Guid orderId = await system.PlaceOrderAsync(variant);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");

        (await PaymentStatusAsync(system, orderId)).ShouldBe(PaymentStatus.Authorized);
        (await ReservationStatusAsync(system, orderId)).ShouldBe(ReservationStatus.Held);
        (await ShipmentStatusAsync(system, orderId)).ShouldBe(ShipmentStatus.Scheduled);

        IReadOnlyList<string> timeline = await system.GetTimelineAsync(orderId);
        timeline.ShouldBe(
        [
            nameof(OrderCreated),
            nameof(PaymentAuthorized),
            nameof(InventoryReserved),
            nameof(ShipmentScheduled),
        ]);
    }

    [Theory]
    [InlineData(SagaVariant.Orchestrated)]
    [InlineData(SagaVariant.Choreographed)]
    public async Task A_declined_payment_cancels_the_order_and_nothing_downstream_runs(SagaVariant variant)
    {
        // The one failure with nothing to undo. A reservation or a shipment appearing here would mean a
        // service acted on an order whose payment never went through.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, $"decline_{variant}");
        system.SetFaults(ServiceName.Payment, rate: 1.0, FaultMode.Decline);

        Guid orderId = await system.PlaceOrderAsync(variant);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Cancelled");

        (await PaymentStatusAsync(system, orderId)).ShouldBe(PaymentStatus.Declined);
        (await ReservationStatusAsync(system, orderId)).ShouldBeNull();
        (await ShipmentStatusAsync(system, orderId)).ShouldBeNull();

        JsonElement order = await system.GetOrderAsync(orderId);
        order.GetProperty("cancellationReason").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(SagaVariant.Orchestrated)]
    [InlineData(SagaVariant.Choreographed)]
    public async Task Unavailable_stock_refunds_the_payment(SagaVariant variant)
    {
        // One step committed, so exactly one step is undone. Stock is set to zero rather than injected as
        // a fault, so this is a real business outcome rather than a simulated one.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, $"nostock_{variant}");

        Guid sku = Guid.CreateVersion7();
        await system.SetStockAsync(sku, quantity: 0);

        Guid orderId = await system.PlaceOrderAsync(variant, new OrderLine(sku, 1, 40m));

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Cancelled");

        await SagaSystem.WaitForAsync(
            async () => await PaymentStatusAsync(system, orderId) is PaymentStatus.Refunded,
            TimeSpan.FromSeconds(30),
            "the payment to be refunded");

        (await ReservationStatusAsync(system, orderId)).ShouldBeNull();
        (await ShipmentStatusAsync(system, orderId)).ShouldBeNull();
    }

    [Theory]
    [InlineData(SagaVariant.Orchestrated)]
    [InlineData(SagaVariant.Choreographed)]
    public async Task A_failed_shipment_undoes_both_committed_steps(SagaVariant variant)
    {
        // The headline compensation case. Two services committed something, and both have to give it back.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, $"noship_{variant}");
        system.SetFaults(ServiceName.Shipping, rate: 1.0, FaultMode.Decline);

        Guid sku = Guid.CreateVersion7();
        await system.SetStockAsync(sku, quantity: 10);

        Guid orderId = await system.PlaceOrderAsync(variant, new OrderLine(sku, 3, 15m));

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Cancelled");

        await SagaSystem.WaitForAsync(
            async () => await PaymentStatusAsync(system, orderId) is PaymentStatus.Refunded
                && await ReservationStatusAsync(system, orderId) is ReservationStatus.Released,
            TimeSpan.FromSeconds(30),
            "both compensations to complete");

        // The stock came back. A release that only flipped a status would leave the shelf short.
        (await StockAsync(system, sku)).ShouldBe(10);

        (await ShipmentStatusAsync(system, orderId)).ShouldBeNull();
    }

    [Fact]
    public async Task An_operator_can_unwind_a_completed_order()
    {
        // The only path that cancels a shipment, because nothing in the forward flow runs after shipping.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "unwind");

        Guid sku = Guid.CreateVersion7();
        await system.SetStockAsync(sku, quantity: 5);

        Guid orderId = await system.PlaceOrderAsync(SagaVariant.Orchestrated, new OrderLine(sku, 2, 20m));
        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");

        HttpResponseMessage response = await system.Client(ServiceName.Order)
            .PostAsJsonAsync($"/orders/{orderId}/cancel", new { reason = "Customer changed their mind." });

        response.EnsureSuccessStatusCode();

        await SagaSystem.WaitForAsync(
            async () => await ShipmentStatusAsync(system, orderId) is ShipmentStatus.Cancelled
                && await ReservationStatusAsync(system, orderId) is ReservationStatus.Released
                && await PaymentStatusAsync(system, orderId) is PaymentStatus.Refunded,
            TimeSpan.FromSeconds(30),
            "all three compensations to complete");

        (await StockAsync(system, sku)).ShouldBe(5);
    }

    [Fact]
    public async Task Choreography_refuses_to_unwind_a_completed_order()
    {
        // Not a limitation of this implementation, a limitation of the pattern: there is nobody to ask.
        // Saying so is more useful than pretending otherwise.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "unwind_choreo");

        Guid orderId = await system.PlaceOrderAsync(SagaVariant.Choreographed);
        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");

        HttpResponseMessage response = await system.Client(ServiceName.Order)
            .PostAsJsonAsync($"/orders/{orderId}/cancel", new { reason = "Nope." });

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
    }

    private static Task<PaymentStatus?> PaymentStatusAsync(SagaSystem system, Guid orderId) =>
        system.QueryAsync<PaymentDbContext, PaymentStatus?>(ServiceName.Payment, async context =>
            await context.Payments
                .AsNoTracking()
                .Where(payment => payment.OrderId == orderId)
                .Select(payment => (PaymentStatus?)payment.Status)
                .FirstOrDefaultAsync());

    private static Task<ReservationStatus?> ReservationStatusAsync(SagaSystem system, Guid orderId) =>
        system.QueryAsync<InventoryDbContext, ReservationStatus?>(ServiceName.Inventory, async context =>
            await context.Reservations
                .AsNoTracking()
                .Where(reservation => reservation.OrderId == orderId)
                .Select(reservation => (ReservationStatus?)reservation.Status)
                .FirstOrDefaultAsync());

    private static Task<ShipmentStatus?> ShipmentStatusAsync(SagaSystem system, Guid orderId) =>
        system.QueryAsync<ShippingDbContext, ShipmentStatus?>(ServiceName.Shipping, async context =>
            await context.Shipments
                .AsNoTracking()
                .Where(shipment => shipment.OrderId == orderId)
                .Select(shipment => (ShipmentStatus?)shipment.Status)
                .FirstOrDefaultAsync());

    private static Task<int> StockAsync(SagaSystem system, Guid sku) =>
        system.QueryAsync<InventoryDbContext, int>(ServiceName.Inventory, async context =>
            await context.Stock
                .AsNoTracking()
                .Where(item => item.Sku == sku)
                .Select(item => item.Available)
                .FirstAsync());
}
