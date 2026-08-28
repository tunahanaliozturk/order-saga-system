using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.BuildingBlocks.Messaging;

namespace OrderSaga.BuildingBlocks;

/// <summary>Registration for the messaging plumbing every service shares.</summary>
public static class BuildingBlocksExtensions
{
    /// <summary>
    /// Registers the outbox, the idempotency ledger, and the diagnostics every service needs.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <typeparam name="TContext">The service's own database context.</typeparam>
    public static IServiceCollection AddServiceMessaging<TContext>(
        this IServiceCollection services,
        IConfiguration configuration)
        where TContext : ServiceDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<MessageTypeRegistry>();
        services.TryAddSingleton<SagaDiagnostics>();

        // The plumbing works against the base type so it does not have to know which service it is in.
        // Both resolve to the same instance, so the outbox row and the business row share a transaction.
        services.TryAddScoped<ServiceDbContext>(provider => provider.GetRequiredService<TContext>());
        services.TryAddScoped<IOutboxWriter, OutboxWriter>();
        services.TryAddScoped<IdempotencyGuard>();

        return services;
    }

    /// <summary>
    /// Starts the outbox relay.
    /// </summary>
    /// <remarks>
    /// Registered after the bus on purpose. Hosted services start in registration order, and a relay that
    /// starts first will happily publish into an exchange whose queues have not been declared yet. RabbitMQ
    /// drops those messages without an error anywhere, which surfaces much later as an event that simply
    /// never happened.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    public static IServiceCollection AddOutboxRelay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<OutboxRelay>();
        return services;
    }

    /// <summary>Applies pending migrations. Suitable for a demo, not for several instances rolling at once.</summary>
    /// <param name="services">Application services.</param>
    /// <typeparam name="TContext">The service's own database context.</typeparam>
    public static async Task MigrateAsync<TContext>(this IServiceProvider services)
        where TContext : ServiceDbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TContext>().Database.MigrateAsync();
    }
}
