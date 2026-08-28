// One command brings up Postgres, RabbitMQ, all four services, and the dashboard. The dashboard is the
// point: a single trace shows one order crossing four services, which is the fastest way to understand
// what either coordination strategy actually does.
IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithDataVolume();

// Database per service, on one server. Separate servers would prove nothing extra and cost four
// containers: what matters is that no service can see another's schema, and separate databases do that.
IResourceBuilder<PostgresDatabaseResource> orderDb = postgres.AddDatabase("orderdb");
IResourceBuilder<PostgresDatabaseResource> paymentDb = postgres.AddDatabase("paymentdb");
IResourceBuilder<PostgresDatabaseResource> inventoryDb = postgres.AddDatabase("inventorydb");
IResourceBuilder<PostgresDatabaseResource> shippingDb = postgres.AddDatabase("shippingdb");

IResourceBuilder<RabbitMQServerResource> rabbit = builder
    .AddRabbitMQ("rabbitmq")
    .WithManagementPlugin();

builder.AddProject<Projects.OrderSaga_OrderService>("order-service")
    .WithSagaDefaults(orderDb, rabbit)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.OrderSaga_PaymentService>("payment-service")
    .WithSagaDefaults(paymentDb, rabbit)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.OrderSaga_InventoryService>("inventory-service")
    .WithSagaDefaults(inventoryDb, rabbit)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.OrderSaga_ShippingService>("shipping-service")
    .WithSagaDefaults(shippingDb, rabbit)
    .WithHttpHealthCheck("/health/ready");

await builder.Build().RunAsync();

/// <summary>Shared wiring for the four services.</summary>
internal static class AppHostExtensions
{
    /// <summary>Gives a service its own database, the shared broker, and startup migrations.</summary>
    /// <param name="project">The project resource.</param>
    /// <param name="database">The service's database.</param>
    /// <param name="rabbit">The broker.</param>
    public static IResourceBuilder<ProjectResource> WithSagaDefaults(
        this IResourceBuilder<ProjectResource> project,
        IResourceBuilder<PostgresDatabaseResource> database,
        IResourceBuilder<RabbitMQServerResource> rabbit) =>
        project
            .WithReference(database)
            .WaitFor(database)
            .WithReference(rabbit)
            .WaitFor(rabbit)

            // Each service owns its own schema and migrates it on startup. That suits one instance per
            // service, which is what this host runs. A real deployment migrates as a separate step so
            // several instances rolling at once cannot race each other into the same lock.
            .WithEnvironment("OrderSaga__MigrateOnStartup", "true");
}
