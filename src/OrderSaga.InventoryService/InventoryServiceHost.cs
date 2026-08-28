using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.ServiceDefaults;

namespace OrderSaga.InventoryService;

/// <summary>
/// Builds the Inventory service.
/// </summary>
/// <remarks>
/// Separated from the entry point so the integration suite can start and stop this service in process.
/// The chaos tests need to stop one participant mid-saga and bring it back, and doing that against a real
/// host is what proves the saga survives it. A test that only ever ran a mocked consumer would prove
/// nothing about redelivery.
/// </remarks>
public static class InventoryServiceHost
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
        builder.AddSagaParticipant<InventoryDbContext>("inventorydb", Assembly.GetExecutingAssembly());

        builder.Services.Configure<InventoryOptions>(
            builder.Configuration.GetSection(InventoryOptions.SectionName));

        builder.Services.AddSingleton<FaultInjector>();
        builder.Services.AddScoped<InventoryProcessor>();
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (builder.Configuration.GetValue("OrderSaga:MigrateOnStartup", defaultValue: false))
        {
            await app.Services.MigrateAsync<InventoryDbContext>();
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();

        app.MapGet("/inventory/reservations/{orderId:guid}", async (
            Guid orderId,
            InventoryDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            Reservation? reservation = await dbContext.Reservations
                .AsNoTracking()
                .Include(entity => entity.Lines)
                .FirstOrDefaultAsync(entity => entity.OrderId == orderId, cancellationToken);

            return reservation is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    reservation.Id,
                    reservation.OrderId,
                    Status = reservation.Status.ToString(),
                    reservation.CreatedAt,
                    reservation.ReleasedAt,
                    Lines = reservation.Lines.Select(line => new { line.Sku, line.Quantity }),
                });
        })
        .WithName("GetReservationForOrder")
        .WithTags("Inventory");

        app.MapGet("/inventory/stock/{sku:guid}", async (
            Guid sku,
            InventoryDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            StockItem? item = await dbContext.Stock
                .AsNoTracking()
                .FirstOrDefaultAsync(entity => entity.Sku == sku, cancellationToken);

            return item is null ? Results.NotFound() : Results.Ok(new { item.Sku, item.Available });
        })
        .WithName("GetStock")
        .WithTags("Inventory");

        // Seeding, and the only way to make a product genuinely unavailable. Tests set a product to zero rather
        // than relying on an empty catalogue, which is what makes the unavailable path deterministic.
        app.MapPut("/inventory/stock/{sku:guid}", async (
            Guid sku,
            SetStockRequest request,
            InventoryProcessor processor,
            CancellationToken cancellationToken) =>
        {
            if (request.Quantity < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(request.Quantity)] = ["Must not be negative."],
                });
            }

            await processor.SetStockAsync(sku, request.Quantity, cancellationToken);
            return Results.Ok(new { sku, request.Quantity });
        })
        .WithName("SetStock")
        .WithTags("Inventory");

        app.MapPost("/test/inventory/fault-rate", (FaultRateRequest request, FaultInjector faults) =>
        {
            if (request.Rate is < 0 or > 1)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(request.Rate)] = ["Must be between 0 and 1."],
                });
            }

            faults.Configure(request.Rate, request.Mode);
            return Results.Ok(new { rate = faults.Rate, mode = faults.Mode.ToString() });
        })
        .WithName("SetInventoryFaultRate")
        .WithTags("Test");

        return app;
    }
}
