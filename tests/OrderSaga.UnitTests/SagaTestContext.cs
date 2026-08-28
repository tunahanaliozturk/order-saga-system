using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;
using OrderSaga.OrderService;

namespace OrderSaga.UnitTests;

/// <summary>
/// Records what the state machine staged, instead of writing it to a database.
/// </summary>
/// <remarks>
/// The saga's contract with the rest of the system is the set of messages it stages and the state it
/// moves to. Both are observable without a database, so the state-machine suite runs in milliseconds and
/// on any machine. Whether staging and saving are genuinely one transaction is a different question, and
/// it is answered against real Postgres in the integration suite.
/// </remarks>
public sealed class RecordingOutboxWriter : IOutboxWriter
{
    private readonly ConcurrentQueue<ISagaMessage> _staged = new();

    /// <summary>Everything staged so far, in order.</summary>
    public IReadOnlyList<ISagaMessage> Staged => [.. _staged];

    /// <inheritdoc />
    public OutboxMessage Stage<TMessage>(TMessage message)
        where TMessage : ISagaMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        _staged.Enqueue(message);

        return OutboxMessage.Stage(
            message.CorrelationId,
            MessageTypeRegistry.NameOf(message.GetType()),
            MessageTypeRegistry.Serialize(message),
            DateTimeOffset.UtcNow);
    }

    /// <summary>The staged messages of one type.</summary>
    /// <typeparam name="TMessage">Contract type.</typeparam>
    public IReadOnlyList<TMessage> Of<TMessage>()
        where TMessage : ISagaMessage =>
        [.. _staged.OfType<TMessage>()];
}

/// <summary>The state machine, an in-memory bus, and nothing else.</summary>
public sealed class SagaTestContext : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private SagaTestContext(ServiceProvider provider)
    {
        _provider = provider;
        Harness = provider.GetRequiredService<ITestHarness>();
        Saga = provider.GetRequiredService<ISagaStateMachineTestHarness<OrderStateMachine, OrderSagaState>>();
        Outbox = provider.GetRequiredService<RecordingOutboxWriter>();
    }

    /// <summary>The in-memory bus.</summary>
    public ITestHarness Harness { get; }

    /// <summary>The saga under test.</summary>
    public ISagaStateMachineTestHarness<OrderStateMachine, OrderSagaState> Saga { get; }

    /// <summary>What the saga staged for publication.</summary>
    public RecordingOutboxWriter Outbox { get; }

    /// <summary>The state machine instance, for referring to states by name.</summary>
    public OrderStateMachine Machine => _provider.GetRequiredService<OrderStateMachine>();

    /// <summary>Starts a harness.</summary>
    public static async Task<SagaTestContext> StartAsync()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton(TimeProvider.System)
            .AddMetrics()
            .AddSingleton<SagaDiagnostics>()
            .AddSingleton<RecordingOutboxWriter>()
            .AddScoped<IOutboxWriter>(services => services.GetRequiredService<RecordingOutboxWriter>())
            .AddMassTransitTestHarness(bus =>
                bus.AddSagaStateMachine<OrderStateMachine, OrderSagaState>())
            .BuildServiceProvider(validateScopes: true);

        var context = new SagaTestContext(provider);
        await context.Harness.Start();
        return context;
    }

    /// <summary>Publishes the orchestrator's start message and waits for the saga to pick it up.</summary>
    /// <param name="orderId">Order id.</param>
    /// <param name="total">Order value.</param>
    public async Task PlaceOrderAsync(Guid orderId, decimal total = 100m)
    {
        await Harness.Bus.Publish(new StartOrderSaga(
            orderId,
            SagaVariant.Orchestrated,
            Guid.NewGuid(),
            total,
            [new OrderLine(Guid.NewGuid(), 1, total)],
            DateTimeOffset.UtcNow));

        await Harness.Consumed.Any<StartOrderSaga>();
    }

    /// <summary>Publishes a message and waits for the saga to consume it.</summary>
    /// <param name="message">The message.</param>
    /// <typeparam name="TMessage">Contract type.</typeparam>
    public async Task DeliverAsync<TMessage>(TMessage message)
        where TMessage : class, ISagaMessage
    {
        await Harness.Bus.Publish(message);
        await Harness.Consumed.Any<TMessage>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Harness.Stop();
        await _provider.DisposeAsync();
    }
}
