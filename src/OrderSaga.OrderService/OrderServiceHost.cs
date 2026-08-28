using System.Reflection;
using MassTransit;
using OrderSaga.BuildingBlocks;
using OrderSaga.ServiceDefaults;

namespace OrderSaga.OrderService;

/// <summary>
/// Builds the Order service.
/// </summary>
/// <remarks>
/// Separated from the entry point so the integration suite can start and stop this service in process.
/// The chaos tests need to stop one participant mid-saga and bring it back, and doing that against a real
/// host is what proves the saga survives it. A test that only ever ran a mocked consumer would prove
/// nothing about redelivery.
/// </remarks>
public static class OrderServiceHost
{
    /// <summary>Builds the application.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configure">Applied to the builder before it is built.</param>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Applied before anything reads configuration, so a test can point the service at its own
        // database and its own broker virtual host without the service knowing it is under test.
        configure?.Invoke(builder);

        builder.AddServiceDefaults();
        builder.AddSagaParticipant<OrderDbContext>(
            "orderdb",
            Assembly.GetExecutingAssembly(),
            bus => bus.AddSagaStateMachine<OrderStateMachine, OrderSagaState>()
                .EntityFrameworkRepository(repository =>
                {
                    // Optimistic concurrency, because two events for the same order can be consumed at the same
                    // time. The pessimistic alternative would serialise them behind a row lock and turn every
                    // compensation into a queue.
                    repository.ConcurrencyMode = ConcurrencyMode.Optimistic;

                    // The same scoped context the outbox writer uses, which is what lets the state machine stage
                    // its outgoing commands in the transaction that saves the saga instance.
                    repository.ExistingDbContext<OrderDbContext>();
                }));

        builder.Services.Configure<StuckOrderOptions>(
            builder.Configuration.GetSection(StuckOrderOptions.SectionName));

        builder.Services.AddScoped<OrderOperations>();
        builder.Services.AddHostedService<StuckOrderSweeper>();
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (builder.Configuration.GetValue("OrderSaga:MigrateOnStartup", defaultValue: false))
        {
            await app.Services.MigrateAsync<OrderDbContext>();
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();
        app.MapOrderRoutes();

        return app;
    }
}
