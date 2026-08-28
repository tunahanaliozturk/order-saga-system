using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;
using OrderSaga.InventoryService;
using OrderSaga.OrderService;
using OrderSaga.PaymentService;
using OrderSaga.ShippingService;

namespace OrderSaga.IntegrationTests;

/// <summary>Which participant a test wants to talk to, stop, or start.</summary>
public enum ServiceName
{
    /// <summary>The entry point, and the orchestrator.</summary>
    Order,

    /// <summary>Payment.</summary>
    Payment,

    /// <summary>Inventory.</summary>
    Inventory,

    /// <summary>Shipping.</summary>
    Shipping,
}

/// <summary>
/// All four services, running in one process against real infrastructure.
/// </summary>
/// <remarks>
/// <para>
/// In process rather than in containers, so a test can stop one participant at an exact point in a saga
/// and bring it back. Killing a container is more dramatic but harder to time, and what actually needs
/// proving is that an unacked message is redelivered and the saga resumes, which a host stop reproduces
/// faithfully: the connection drops with work in flight and the broker requeues it.
/// </para>
/// <para>
/// What this does not reproduce is a process killed between a database commit and the acknowledgement.
/// The outbox durability test covers that case directly instead, by committing and then never letting the
/// relay run.
/// </para>
/// </remarks>
public sealed class SagaSystem : IAsyncDisposable
{
    private readonly Dictionary<ServiceName, ServiceInstance> _services = [];
    private readonly Dictionary<ServiceName, string> _databases = [];
    private readonly string _brokerUri;

    private SagaSystem(string brokerUri) => _brokerUri = brokerUri;

    /// <summary>Serialisation used when reading API responses.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Starts the whole system on its own databases and broker virtual host.</summary>
    /// <param name="containers">Shared containers.</param>
    /// <param name="name">Unique name for this run.</param>
    public static async Task<SagaSystem> StartAsync(ContainerFixture containers, string name)
    {
        ArgumentNullException.ThrowIfNull(containers);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string brokerUri = await containers.CreateVirtualHostAsync($"{name}_{suffix}");

        var system = new SagaSystem(brokerUri);

        foreach (ServiceName service in Enum.GetValues<ServiceName>())
        {
            string database = service.ToString().ToLowerInvariant() + "db";
            system._databases[service] = await containers.CreateDatabaseAsync($"{database}_{name}_{suffix}");
        }

        // Order last: it hosts the saga, and starting the participants first means their queues exist
        // before the first command goes out. Nothing depends on this for correctness, it just avoids a
        // round of redelivery on every test.
        foreach (ServiceName service in new[]
        {
            ServiceName.Payment,
            ServiceName.Inventory,
            ServiceName.Shipping,
            ServiceName.Order,
        })
        {
            await system.StartServiceAsync(service);
        }

        return system;
    }

    /// <summary>The connection string for one service's database.</summary>
    /// <remarks>
    /// Exposed so a test can reach into a service's database while that service is stopped, which is how
    /// the outbox durability test simulates a process that committed and then died before publishing.
    /// </remarks>
    /// <param name="service">Which service.</param>
    public string ConnectionString(ServiceName service) => _databases[service];

    /// <summary>An HTTP client for one service.</summary>
    /// <param name="service">Which service.</param>
    public HttpClient Client(ServiceName service) => Instance(service).Client;

    /// <summary>Runs work against one service's database.</summary>
    /// <param name="service">Which service.</param>
    /// <param name="work">What to do.</param>
    /// <typeparam name="TContext">The service's context type.</typeparam>
    /// <typeparam name="TResult">Result type.</typeparam>
    public async Task<TResult> QueryAsync<TContext, TResult>(
        ServiceName service,
        Func<TContext, Task<TResult>> work)
        where TContext : ServiceDbContext
    {
        ArgumentNullException.ThrowIfNull(work);

        await using AsyncServiceScope scope = Instance(service).App.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TContext>());
    }

    /// <summary>Sets a service's fault dial.</summary>
    /// <param name="service">Which service.</param>
    /// <param name="rate">Probability in [0, 1].</param>
    /// <param name="mode">How it should fail.</param>
    public void SetFaults(ServiceName service, double rate, FaultMode mode = FaultMode.Decline) =>
        Instance(service).App.Services.GetRequiredService<FaultInjector>().Configure(rate, mode);

    /// <summary>Stops a service, leaving anything it was working on unacknowledged.</summary>
    /// <param name="service">Which service.</param>
    public async Task StopServiceAsync(ServiceName service)
    {
        if (!_services.Remove(service, out ServiceInstance? instance))
        {
            return;
        }

        instance.Client.Dispose();

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await instance.App.StopAsync(shutdown.Token);
        await instance.App.DisposeAsync();
    }

    /// <summary>Starts a service that is not currently running.</summary>
    /// <param name="service">Which service.</param>
    public async Task StartServiceAsync(ServiceName service)
    {
        if (_services.ContainsKey(service))
        {
            return;
        }

        WebApplication app = await BuildAsync(service);
        await app.StartAsync();

        string address = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

        _services[service] = new ServiceInstance(
            app,
            new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) });
    }

    /// <summary>Places an order and returns its id.</summary>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="lines">What to order.</param>
    public async Task<Guid> PlaceOrderAsync(SagaVariant variant, params OrderLine[] lines)
    {
        OrderLine[] contents = lines.Length > 0
            ? lines
            : [new OrderLine(Guid.CreateVersion7(), 1, 25m)];

        string route = variant is SagaVariant.Orchestrated ? "/orders" : "/orders/choreographed";

        HttpResponseMessage response = await Client(ServiceName.Order)
            .PostAsJsonAsync(route, new { customerId = Guid.CreateVersion7(), lines = contents }, Json);

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>Sets a product's stock, which is how a test makes it genuinely unavailable.</summary>
    /// <param name="sku">Product.</param>
    /// <param name="quantity">Units on hand.</param>
    public async Task SetStockAsync(Guid sku, int quantity)
    {
        HttpResponseMessage response = await Client(ServiceName.Inventory)
            .PutAsJsonAsync($"/inventory/stock/{sku}", new { quantity }, Json);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Publishes a message directly, with a message id the caller controls.
    /// </summary>
    /// <remarks>
    /// The duplicate-delivery tests need the same message id on every copy, because that is what a broker
    /// redelivery looks like and what the idempotency ledger keys on. Letting MassTransit generate a fresh
    /// id per publish would produce ten different messages and prove nothing.
    /// </remarks>
    /// <param name="from">Which service's bus to publish from.</param>
    /// <param name="message">The message.</param>
    /// <param name="messageId">The id every copy carries.</param>
    /// <param name="copies">How many copies to publish concurrently.</param>
    /// <typeparam name="TMessage">Contract type.</typeparam>
    public async Task PublishAsync<TMessage>(
        ServiceName from,
        TMessage message,
        Guid messageId,
        int copies = 1)
        where TMessage : class, ISagaMessage
    {
        IPublishEndpoint publisher = Instance(from).App.Services.GetRequiredService<IPublishEndpoint>();

        await Task.WhenAll(Enumerable.Range(0, copies).Select(_ =>
            publisher.Publish(
                message,
                Pipe.Execute<PublishContext<TMessage>>(context =>
                {
                    context.MessageId = messageId;
                    context.CorrelationId = message.CorrelationId;
                }))));
    }

    /// <summary>Reads an order back.</summary>
    /// <param name="orderId">Order.</param>
    public async Task<JsonElement> GetOrderAsync(Guid orderId)
    {
        HttpResponseMessage response = await Client(ServiceName.Order).GetAsync($"/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>Reads an order's timeline, oldest first.</summary>
    /// <param name="orderId">Order.</param>
    public async Task<IReadOnlyList<string>> GetTimelineAsync(Guid orderId)
    {
        HttpResponseMessage response = await Client(ServiceName.Order)
            .GetAsync($"/orders/{orderId}/timeline");

        response.EnsureSuccessStatusCode();

        JsonElement entries = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        return
        [
            .. entries.EnumerateArray().Select(entry => entry.GetProperty("eventType").GetString() ?? string.Empty)
        ];
    }

    /// <summary>Waits for an order to reach a terminal status and returns it.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="timeout">How long to wait.</param>
    public async Task<string> WaitForTerminalAsync(Guid orderId, TimeSpan? timeout = null)
    {
        string status = string.Empty;

        await WaitForAsync(
            async () =>
            {
                JsonElement order = await GetOrderAsync(orderId);
                status = order.GetProperty("status").GetString() ?? string.Empty;
                return status is "Completed" or "Cancelled";
            },
            // Generous on purpose. These are waits on eventual consistency across four services and a
            // broker, not tight assertions, and the first test in a run also pays for cold model building
            // and topology declaration. A tight bound here buys nothing except a flaky suite.
            timeout ?? TimeSpan.FromSeconds(90),
            $"order {orderId} to reach a terminal status");

        return status;
    }

    /// <summary>Polls a condition until it holds or the timeout expires.</summary>
    /// <param name="condition">What to wait for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="description">Included in the failure message.</param>
    public static async Task WaitForAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string description)
    {
        ArgumentNullException.ThrowIfNull(condition);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"Timed out after {timeout} waiting for {description}.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (ServiceName service in _services.Keys.ToArray())
        {
            await StopServiceAsync(service);
        }

        // Every test uses fresh database names, so every test creates fresh pools. Npgsql keeps idle
        // connections in them indefinitely, and without this the suite leaks a pool per test until the
        // server refuses new clients.
        NpgsqlConnection.ClearAllPools();
    }

    private ServiceInstance Instance(ServiceName service) =>
        _services.TryGetValue(service, out ServiceInstance? instance)
            ? instance
            : throw new InvalidOperationException($"The {service} service is not running.");

    private Task<WebApplication> BuildAsync(ServiceName service) => service switch
    {
        ServiceName.Order => OrderServiceHost.BuildAsync([], builder => Configure(builder, service)),
        ServiceName.Payment => PaymentServiceHost.BuildAsync([], builder => Configure(builder, service)),
        ServiceName.Inventory => InventoryServiceHost.BuildAsync([], builder => Configure(builder, service)),
        ServiceName.Shipping => ShippingServiceHost.BuildAsync([], builder => Configure(builder, service)),
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Unknown service."),
    };

    private void Configure(WebApplicationBuilder builder, ServiceName service)
    {
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // Providers are cleared, not just quietened. Four hosts share this process and the suite stops and
        // starts them, and the Windows event-log provider holds a process-wide handle: once one host
        // disposes it, every later log write from any host throws. MassTransit sees that as a consumer
        // fault, retries, and drops the message in the error queue, which shows up as a missing timeline
        // entry and looks exactly like a saga bug.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"ConnectionStrings:{service.ToString().ToLowerInvariant()}db"] = _databases[service],
            ["ConnectionStrings:rabbitmq"] = _brokerUri,
            ["OrderSaga:MigrateOnStartup"] = "true",

            // Fast enough that a test does not wait on the poller, slow enough that it does not spin.
            ["OrderSaga:Outbox:PollInterval"] = "00:00:00.050",
            ["OrderSaga:Outbox:RetentionSweepInterval"] = "00:00:00",

            // Long enough that a healthy order never trips it, short enough that the stuck-order test
            // does not have to wait five minutes to see the flag it is asserting on.
            ["OrderSaga:StuckOrders:Timeout"] = "00:00:05",
            ["OrderSaga:StuckOrders:SweepInterval"] = "00:00:01",
        });
    }

    private sealed record ServiceInstance(WebApplication App, HttpClient Client);
}
