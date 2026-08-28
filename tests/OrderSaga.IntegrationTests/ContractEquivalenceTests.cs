using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.Contracts;
using Shouldly;

namespace OrderSaga.IntegrationTests;

/// <summary>
/// Whether the two coordination strategies actually produce the same outcome, or only look like they do.
/// </summary>
/// <remarks>
/// <para>
/// The point of building both is to compare them, and a comparison is only worth anything if the two are
/// doing the same job. Without these tests, "orchestration and choreography behave equivalently" would be
/// a claim in a README rather than something that fails a build when it stops being true.
/// </para>
/// <para>
/// The assertion is on the set of events and the terminal state, not on their order. Choreography has no
/// coordinator, so two participants compensating themselves in parallel can report in either order. Both
/// orders are correct, and demanding one of them would be testing an implementation detail.
/// </para>
/// </remarks>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class ContractEquivalenceTests(ContainerFixture containers)
{
    [Fact]
    public async Task Both_strategies_produce_the_same_story_for_a_successful_order()
    {
        Outcome orchestrated = await RunAsync("equiv_happy_o", SagaVariant.Orchestrated, ScenarioFault.None);
        Outcome choreographed = await RunAsync("equiv_happy_c", SagaVariant.Choreographed, ScenarioFault.None);

        choreographed.Status.ShouldBe(orchestrated.Status);
        choreographed.Status.ShouldBe("Completed");
        choreographed.Events.ShouldBe(orchestrated.Events);
    }

    [Fact]
    public async Task Both_strategies_produce_the_same_story_when_payment_is_declined()
    {
        Outcome orchestrated = await RunAsync("equiv_decl_o", SagaVariant.Orchestrated, ScenarioFault.Payment);
        Outcome choreographed = await RunAsync("equiv_decl_c", SagaVariant.Choreographed, ScenarioFault.Payment);

        choreographed.Status.ShouldBe(orchestrated.Status);
        choreographed.Status.ShouldBe("Cancelled");
        choreographed.Events.ShouldBe(orchestrated.Events);
    }

    [Fact]
    public async Task Both_strategies_fire_the_same_compensations_when_the_shipment_fails()
    {
        // The case where the two designs differ most: one is told what to undo, the other works it out
        // from an event each participant happens to be subscribed to. The observable result has to match.
        Outcome orchestrated = await RunAsync("equiv_ship_o", SagaVariant.Orchestrated, ScenarioFault.Shipping);
        Outcome choreographed = await RunAsync("equiv_ship_c", SagaVariant.Choreographed, ScenarioFault.Shipping);

        choreographed.Status.ShouldBe(orchestrated.Status);
        choreographed.Status.ShouldBe("Cancelled");

        orchestrated.Events.ShouldContain(nameof(PaymentRefunded));
        orchestrated.Events.ShouldContain(nameof(InventoryReleased));
        choreographed.Events.ShouldBe(orchestrated.Events);
    }

    private async Task<Outcome> RunAsync(string name, SagaVariant variant, ScenarioFault fault)
    {
        await using SagaSystem system = await SagaSystem.StartAsync(containers, name);

        switch (fault)
        {
            case ScenarioFault.Payment:
                system.SetFaults(ServiceName.Payment, rate: 1.0, FaultMode.Decline);
                break;

            case ScenarioFault.Shipping:
                system.SetFaults(ServiceName.Shipping, rate: 1.0, FaultMode.Decline);
                break;

            case ScenarioFault.None:
            default:
                break;
        }

        Guid sku = Guid.CreateVersion7();
        await system.SetStockAsync(sku, quantity: 5);

        Guid orderId = await system.PlaceOrderAsync(variant, new OrderLine(sku, 1, 35m));
        string status = await system.WaitForTerminalAsync(orderId);

        // The timeline keeps growing after the order goes terminal, because compensations report back
        // afterwards. Waiting for it to settle is the difference between comparing two complete stories
        // and comparing one complete story with a snapshot of another taken too early.
        IReadOnlyList<string> events = await SettledTimelineAsync(system, orderId);

        return new Outcome(status, [.. events.Order(StringComparer.Ordinal)]);
    }

    private static async Task<IReadOnlyList<string>> SettledTimelineAsync(SagaSystem system, Guid orderId)
    {
        IReadOnlyList<string> previous = [];

        for (int attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(250);
            IReadOnlyList<string> current = await system.GetTimelineAsync(orderId);

            if (current.Count == previous.Count && current.Count > 0)
            {
                return current;
            }

            previous = current;
        }

        return previous;
    }

    private enum ScenarioFault
    {
        None,
        Payment,
        Shipping,
    }

    private sealed record Outcome(string Status, IReadOnlyList<string> Events);
}
