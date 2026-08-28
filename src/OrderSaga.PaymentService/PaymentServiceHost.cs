using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.ServiceDefaults;

namespace OrderSaga.PaymentService;

/// <summary>
/// Builds the Payment service.
/// </summary>
/// <remarks>
/// Separated from the entry point so the integration suite can start and stop this service in process.
/// The chaos tests need to stop one participant mid-saga and bring it back, and doing that against a real
/// host is what proves the saga survives it. A test that only ever ran a mocked consumer would prove
/// nothing about redelivery.
/// </remarks>
public static class PaymentServiceHost
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
        builder.AddSagaParticipant<PaymentDbContext>("paymentdb", Assembly.GetExecutingAssembly());

        builder.Services.AddSingleton<FaultInjector>();
        builder.Services.AddScoped<PaymentProcessor>();
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();

        WebApplication app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (builder.Configuration.GetValue("OrderSaga:MigrateOnStartup", defaultValue: false))
        {
            await app.Services.MigrateAsync<PaymentDbContext>();
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.MapHealthEndpoints();

        app.MapGet("/payments/{orderId:guid}", async (
            Guid orderId,
            PaymentDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            Payment? payment = await dbContext.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(entity => entity.OrderId == orderId, cancellationToken);

            return payment is null
                ? Results.NotFound()
                : Results.Ok(new
                {
                    payment.Id,
                    payment.OrderId,
                    payment.Amount,
                    Status = payment.Status.ToString(),
                    payment.DeclineReason,
                    payment.CreatedAt,
                    payment.RefundedAt,
                });
        })
        .WithName("GetPaymentForOrder")
        .WithTags("Payments");

        // The fault dial. A runtime knob rather than a build flag, so the code exercised by the chaos suite is
        // the same code that runs in the demo.
        app.MapPost("/test/payment/fault-rate", (FaultRateRequest request, FaultInjector faults) =>
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
        .WithName("SetPaymentFaultRate")
        .WithTags("Test");

        return app;
    }
}
