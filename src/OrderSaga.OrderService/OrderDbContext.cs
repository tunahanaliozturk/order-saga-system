using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OrderSaga.BuildingBlocks.Messaging;

namespace OrderSaga.OrderService;

/// <summary>The Order service's own database, which also holds the orchestrator's saga state.</summary>
/// <param name="options">Provider options.</param>
public sealed class OrderDbContext(DbContextOptions<OrderDbContext> options) : ServiceDbContext(options)
{
    /// <summary>Orders as the customer sees them.</summary>
    public DbSet<Order> Orders => Set<Order>();

    /// <summary>The cross-service story of each order.</summary>
    public DbSet<OrderTimelineEntry> Timeline => Set<OrderTimelineEntry>();

    /// <summary>Saga instances, for orchestrated orders.</summary>
    public DbSet<OrderSagaState> SagaState => Set<OrderSagaState>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("orders");
            order.HasKey(entity => entity.Id);

            order.Property(entity => entity.Total).HasPrecision(18, 2);
            order.Property(entity => entity.LinesJson).HasColumnType("jsonb").IsRequired();
            order.Property(entity => entity.Status).HasConversion<int>();
            order.Property(entity => entity.Variant).HasConversion<int>();
            order.Property(entity => entity.CancellationReason).HasMaxLength(512);

            order.Ignore(entity => entity.IsTerminal);

            // The stuck-order sweep and the stuck-order endpoint both scan for non-terminal orders older
            // than the timeout, which is what this index is sized for.
            order.HasIndex(entity => new { entity.Status, entity.CreatedAt });
        });

        modelBuilder.Entity<OrderTimelineEntry>(entry =>
        {
            entry.ToTable("order_timeline");
            entry.HasKey(entity => entity.Id);

            entry.Property(entity => entity.ServiceName).HasMaxLength(64).IsRequired();
            entry.Property(entity => entity.EventType).HasMaxLength(128).IsRequired();
            entry.Property(entity => entity.PayloadSnapshot).HasColumnType("jsonb").IsRequired();

            // Reading one order's story, oldest first.
            entry.HasIndex(entity => new { entity.OrderId, entity.OccurredAt });
        });

        // Saga state as typed columns. A shape change becomes a migration that CI can catch, rather than
        // a deserialisation failure that only shows up against real in-flight instances in production.
        ((ISagaClassMap)new OrderSagaStateMap()).Configure(modelBuilder);
    }
}

/// <summary>Lets the EF tooling build a context without booting the service.</summary>
public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext>
{
    /// <inheritdoc />
    public OrderDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OrderDbContext>()
            .UseNpgsql("Host=localhost;Database=orderdb;Username=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
