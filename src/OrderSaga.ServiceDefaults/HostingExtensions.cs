using System.Reflection;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OrderSaga.BuildingBlocks;
using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;

namespace OrderSaga.ServiceDefaults;

/// <summary>
/// The setup all four services share: database, broker, outbox, telemetry, health.
/// </summary>
/// <remarks>
/// Each service owns its own database and its own queues, but the way it connects to them is identical.
/// Repeating it four times is how three services end up with a retry policy and one does not.
/// </remarks>
public static class HostingExtensions
{
    /// <summary>Health-check tag that gates readiness.</summary>
    public const string ReadinessTag = "ready";

    /// <summary>MassTransit's activity and meter source name.</summary>
    private const string MassTransitSourceName = "MassTransit";

    /// <summary>
    /// How many orders a service processes at once.
    /// </summary>
    /// <remarks>
    /// Also the ceiling on parallelism per service, since every message for one order lands on one
    /// partition. Sized for a demo; a real deployment picks it from the consumer's concurrency budget.
    /// </remarks>
    private const int PartitionCount = 16;

    /// <summary>Adds telemetry and the liveness check.</summary>
    /// <param name="builder">Host builder.</param>
    /// <typeparam name="TBuilder">Builder type.</typeparam>
    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(SagaDiagnostics.SourceName)
                .AddMeter(MassTransitSourceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(SagaDiagnostics.SourceName)

                // MassTransit propagates W3C trace context through message headers, so one order is one
                // trace across all four services rather than four disconnected ones.
                .AddSource(MassTransitSourceName));

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        builder.Services.AddHealthChecks()
            .AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

        builder.Services.AddServiceDiscovery();

        return builder;
    }

    /// <summary>
    /// Wires a service's database, its outbox, and its bus.
    /// </summary>
    /// <param name="builder">Host builder.</param>
    /// <param name="connectionStringName">Name of the service's database connection string.</param>
    /// <param name="consumerAssembly">Assembly scanned for consumers.</param>
    /// <param name="configureBus">Extra bus registration, for the service that hosts the saga.</param>
    /// <typeparam name="TContext">The service's own database context.</typeparam>
    public static IHostApplicationBuilder AddSagaParticipant<TContext>(
        this IHostApplicationBuilder builder,
        string connectionStringName,
        Assembly consumerAssembly,
        Action<IBusRegistrationConfigurator>? configureBus = null)
        where TContext : ServiceDbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);
        ArgumentNullException.ThrowIfNull(consumerAssembly);

        string database = builder.Configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' is not configured.");

        builder.Services.AddDbContext<TContext>(options => options
            .UseNpgsql(database, npgsql => npgsql.MigrationsAssembly(typeof(TContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention());

        builder.Services.AddServiceMessaging<TContext>(builder.Configuration);

        // Without this the bus starts in the background and the host reports itself started before its
        // queues and bindings exist. Anything published in that window is discarded by the broker with no
        // error raised anywhere.
        builder.Services.AddOptions<MassTransitHostOptions>().Configure(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(30);
            options.StopTimeout = TimeSpan.FromSeconds(15);
        });

        builder.Services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            bus.AddConsumers(consumerAssembly);
            configureBus?.Invoke(bus);

            bus.UsingRabbitMq((context, rabbit) =>
            {
                rabbit.Host(new Uri(
                    builder.Configuration.GetConnectionString("rabbitmq")
                    ?? throw new InvalidOperationException("Connection string 'rabbitmq' is not configured.")));

                // Transport-level transient handling only. Anything that survives these attempts is a
                // business failure, and business failures are the saga's job to compensate, not the
                // broker's job to retry.
                rabbit.UseMessageRetry(retry => retry.Interval(3, TimeSpan.FromMilliseconds(500)));

                // One order is handled one message at a time; different orders run in parallel. Without
                // this, a consumer processes an order's events concurrently, and two things break in ways
                // that are hard to spot: the timeline records them in whatever order the transactions
                // happen to commit, and two events racing on the same saga instance collide on the
                // concurrency token and one of them has to be retried.
                //
                // Partitioning by correlation id keeps the ordering guarantee without giving up
                // throughput, which serialising the whole endpoint would.
                rabbit.UsePartitioner(PartitionCount, context => context.CorrelationId ?? Guid.Empty);

                rabbit.ConfigureEndpoints(context);
            });
        });

        // After the bus, so the relay cannot publish before the topology it publishes into exists.
        builder.Services.AddOutboxRelay();

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TContext>("database", tags: [ReadinessTag]);

        return builder;
    }

    /// <summary>Maps the liveness and readiness endpoints.</summary>
    /// <param name="app">Web application.</param>
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("live"),
        });

        // MassTransit registers its own bus health check, so readiness covers the broker as well as the
        // database without a second package.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = static registration =>
                registration.Tags.Contains(ReadinessTag) || registration.Name.Contains("masstransit", StringComparison.OrdinalIgnoreCase),
        });

        return app;
    }
}
