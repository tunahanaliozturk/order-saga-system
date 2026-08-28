using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;
using OrderSaga.OrderService;
using OrderSaga.PaymentService;
using Shouldly;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// The three properties that make at-least-once delivery survivable, each broken on purpose first.
/// </summary>
/// <remarks>
/// Everything here is about what happens when something goes wrong, because that is the only part of a
/// saga that is hard. The happy path is a sequence of method calls; the rest of this file is the reason
/// the pattern exists.
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class ResilienceTests(ContainerFixture containers)
{
    [Fact]
    public async Task Ten_copies_of_one_message_produce_one_charge()
    {
        // What at-least-once delivery actually looks like in production, compressed: the same message id
        // delivered ten times at once. Nine of those inserts lose the race on the unique constraint, and
        // losing that race is the mechanism, not an error.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "duplicates");

        Guid orderId = Guid.CreateVersion7();
        Guid messageId = Guid.CreateVersion7();

        await system.PublishAsync(
            ServiceName.Order,
            new AuthorizePayment(orderId, SagaVariant.Orchestrated, Guid.CreateVersion7(), 99m),
            messageId,
            copies: 10);

        await SagaSystem.WaitForAsync(
            async () => await PaymentCountAsync(system, orderId) > 0,
            TimeSpan.FromSeconds(30),
            "the payment to be authorized");

        // Give every redelivery time to arrive and be refused before counting.
        await Task.Delay(TimeSpan.FromSeconds(2));

        (await PaymentCountAsync(system, orderId)).ShouldBe(1);

        int ledgerEntries = await system.QueryAsync<PaymentDbContext, int>(
            ServiceName.Payment,
            context => context.ProcessedMessages
                .AsNoTracking()
                .CountAsync(entry => entry.MessageId == messageId));

        ledgerEntries.ShouldBe(1);
    }

    [Fact]
    public async Task A_message_staged_by_a_process_that_died_is_still_published()
    {
        // The dual-write problem, staged deliberately. The Order service commits an order and its outbox
        // row, then stops before the relay can publish. Nothing on the broker knows the order exists.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "outbox_durability");

        await system.StopServiceAsync(ServiceName.Order);

        Guid orderId = await StageOrderDirectlyAsync(system);

        // Nothing has been published, so nothing downstream has happened.
        await Task.Delay(TimeSpan.FromSeconds(1));
        (await PaymentCountAsync(system, orderId)).ShouldBe(0);

        // A new instance comes up, finds the pending row, and publishes it. This is the whole reason the
        // event is a row and not a method call.
        await system.StartServiceAsync(ServiceName.Order);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");
        (await PaymentCountAsync(system, orderId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_order_survives_a_participant_being_down()
    {
        // The queue is durable, so a service that is away for a while has work waiting when it returns.
        // Nothing is dropped, and nothing needs re-driving by hand.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "participant_down");

        await system.StopServiceAsync(ServiceName.Payment);

        Guid orderId = await system.PlaceOrderAsync(SagaVariant.Orchestrated);

        await Task.Delay(TimeSpan.FromSeconds(2));
        (await OrderStatusAsync(system, orderId)).ShouldBe("Created");

        await system.StartServiceAsync(ServiceName.Payment);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");
    }

    [Fact]
    public async Task The_orchestrator_resumes_from_persisted_state_after_a_restart()
    {
        // The orchestrator holds the flow, so killing it is the interesting failure rather than killing a
        // leaf. It comes back with no memory of anything, reads the saga instance out of Postgres, and
        // carries on from the step the order had reached.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "orchestrator_restart");

        Guid orderId = await system.PlaceOrderAsync(SagaVariant.Orchestrated);

        await SagaSystem.WaitForAsync(
            async () => await OrderStatusAsync(system, orderId) is not "Created",
            TimeSpan.FromSeconds(30),
            "the order to get past the first step");

        // Taken down mid-flight. Everything the saga knows is in Postgres; this process knows nothing.
        await system.StopServiceAsync(ServiceName.Order);
        await Task.Delay(TimeSpan.FromSeconds(1));
        await system.StartServiceAsync(ServiceName.Order);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");

        // And it resumed rather than restarting: one charge, not two.
        (await PaymentCountAsync(system, orderId)).ShouldBe(1);
    }

    [Fact]
    public async Task An_order_that_stops_making_progress_is_flagged_rather_than_forgotten()
    {
        // Eventual consistency is only acceptable if "eventually" has a bound. A participant that never
        // comes back has to turn into something alertable instead of an order that sits there quietly.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "stuck");

        await system.StopServiceAsync(ServiceName.Payment);

        Guid orderId = await system.PlaceOrderAsync(SagaVariant.Orchestrated);

        // Waiting on the flag rather than on the endpoint. The endpoint also answers from the clock, so
        // it would report the order before the sweep had run and the test would pass without the sweep
        // working at all.
        await SagaSystem.WaitForAsync(
            async () => (await system.GetOrderAsync(orderId)).GetProperty("isStuck").GetBoolean(),
            TimeSpan.FromSeconds(30),
            "the sweep to flag the order as stuck");

        (await StuckOrderIdsAsync(system)).ShouldContain(orderId);
        (await system.GetOrderAsync(orderId)).GetProperty("status").GetString().ShouldBe("Created");

        // And the flag clears itself once the order starts moving again, rather than needing a human to
        // tidy up after the incident.
        await system.StartServiceAsync(ServiceName.Payment);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");
        (await system.GetOrderAsync(orderId)).GetProperty("isStuck").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Re_driving_a_stuck_order_does_not_duplicate_the_work_it_already_did()
    {
        // The operator nudge. Safe only because every consumer is idempotent: the step that already ran
        // absorbs the repeat instead of charging the customer a second time.
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "retry");

        await system.StopServiceAsync(ServiceName.Inventory);

        Guid orderId = await system.PlaceOrderAsync(SagaVariant.Orchestrated);

        await SagaSystem.WaitForAsync(
            async () => await OrderStatusAsync(system, orderId) is "PaymentAuthorized",
            TimeSpan.FromSeconds(30),
            "the payment step to finish");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            HttpResponseMessage response = await system.Client(ServiceName.Order)
                .PostAsync($"/orders/{orderId}/retry", content: null);

            response.EnsureSuccessStatusCode();
        }

        await system.StartServiceAsync(ServiceName.Inventory);

        (await system.WaitForTerminalAsync(orderId)).ShouldBe("Completed");

        // Three nudges, one charge.
        (await PaymentCountAsync(system, orderId)).ShouldBe(1);
    }

    private static async Task<Guid> StageOrderDirectlyAsync(SagaSystem system)
    {
        DbContextOptions<OrderDbContext> options = new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql(system.ConnectionString(ServiceName.Order))
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var context = new OrderDbContext(options);

        var lines = new List<OrderLine> { new(Guid.CreateVersion7(), 1, 30m) };
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Order order = Order.Place(Guid.CreateVersion7(), lines, SagaVariant.Orchestrated, now);
        context.Orders.Add(order);

        var writer = new OutboxWriter(context, TimeProvider.System);

        // Exactly what the API stages: the announcement everyone hears, and the orchestrator's own trigger.
        writer.Stage(new OrderCreated(
            order.Id,
            SagaVariant.Orchestrated,
            order.CustomerId,
            order.Total,
            lines,
            now));

        writer.Stage(new StartOrderSaga(
            order.Id,
            SagaVariant.Orchestrated,
            order.CustomerId,
            order.Total,
            lines,
            now));

        // One transaction, exactly as a business write would do it.
        await context.SaveChangesAsync();

        return order.Id;
    }

    private static Task<int> PaymentCountAsync(SagaSystem system, Guid orderId) =>
        system.QueryAsync<PaymentDbContext, int>(
            ServiceName.Payment,
            context => context.Payments.AsNoTracking().CountAsync(payment => payment.OrderId == orderId));

    private static async Task<string> OrderStatusAsync(SagaSystem system, Guid orderId) =>
        (await system.GetOrderAsync(orderId)).GetProperty("status").GetString() ?? string.Empty;

    private static async Task<IReadOnlyList<Guid>> StuckOrderIdsAsync(SagaSystem system)
    {
        HttpResponseMessage response = await system.Client(ServiceName.Order).GetAsync("/orders/stuck");
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(SagaSystem.Json);
        return [.. body.EnumerateArray().Select(order => order.GetProperty("id").GetGuid())];
    }
}
