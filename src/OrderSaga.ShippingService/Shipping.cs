using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.ShippingService;

/// <summary>Where a shipment stands.</summary>
public enum ShipmentStatus
{
    /// <summary>Booked with the carrier.</summary>
    Scheduled = 0,

    /// <summary>Unbooked by a compensation.</summary>
    Cancelled = 1,
}

/// <summary>A booking with the carrier for one order.</summary>
public sealed class Shipment
{
    private Shipment()
    {
    }

    /// <summary>Identifier. Handed to the cancel compensation.</summary>
    public Guid Id { get; private set; }

    /// <summary>The order this belongs to.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>Current state.</summary>
    public ShipmentStatus Status { get; private set; }

    /// <summary>How many units are going out.</summary>
    public int ItemCount { get; private set; }

    /// <summary>When it was booked.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it was unbooked.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>Books a shipment.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="itemCount">Units going out.</param>
    /// <param name="now">Current time.</param>
    public static Shipment Schedule(Guid orderId, int itemCount, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            Status = ShipmentStatus.Scheduled,
            ItemCount = itemCount,
            CreatedAt = now,
        };

    /// <summary>Unbooks it. Safe to call on an already cancelled shipment.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>True if this call performed the cancellation.</returns>
    public bool Cancel(DateTimeOffset now)
    {
        if (Status is not ShipmentStatus.Scheduled)
        {
            return false;
        }

        Status = ShipmentStatus.Cancelled;
        CancelledAt = now;
        return true;
    }
}

/// <summary>The Shipping service's own database.</summary>
/// <param name="options">Provider options.</param>
public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options)
    : ServiceDbContext(options)
{
    /// <summary>Carrier bookings.</summary>
    public DbSet<Shipment> Shipments => Set<Shipment>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Shipment>(shipment =>
        {
            shipment.ToTable("shipments");
            shipment.HasKey(entity => entity.Id);
            shipment.Property(entity => entity.Status).HasConversion<int>();

            // One shipment per order, enforced by the database, behind the idempotency ledger.
            shipment.HasIndex(entity => entity.OrderId).IsUnique();
        });
    }
}

/// <summary>Lets the EF tooling build a context without booting the service.</summary>
public sealed class ShippingDbContextFactory : IDesignTimeDbContextFactory<ShippingDbContext>
{
    /// <inheritdoc />
    public ShippingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShippingDbContext>()
            .UseNpgsql("Host=localhost;Database=shippingdb;Username=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}

/// <summary>
/// What the Shipping service does, independent of whether a command or an event asked for it.
/// </summary>
/// <param name="dbContext">The service's context.</param>
/// <param name="outbox">Stages outbound messages in the caller's transaction.</param>
/// <param name="faults">Fault dial.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class ShippingProcessor(
    ShippingDbContext dbContext,
    IOutboxWriter outbox,
    FaultInjector faults,
    TimeProvider timeProvider)
{
    private readonly ShippingDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IOutboxWriter _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

    private readonly FaultInjector _faults = faults ?? throw new ArgumentNullException(nameof(faults));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Books a shipment, or reports failure. Stages the result; the caller commits.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="itemCount">Units going out.</param>
    /// <param name="variant">Coordination strategy.</param>
    public Task ScheduleAsync(Guid orderId, int itemCount, SagaVariant variant)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_faults.ShouldDecline(nameof(ScheduleAsync)))
        {
            // The most interesting failure in the whole system: two steps have already committed, so both
            // have to be undone, in reverse.
            _outbox.Stage(new ShipmentFailed(orderId, variant, "No carrier capacity for this route."));
            return Task.CompletedTask;
        }

        Shipment shipment = Shipment.Schedule(orderId, itemCount, now);

        _dbContext.Shipments.Add(shipment);
        _outbox.Stage(new ShipmentScheduled(orderId, variant, shipment.Id));

        return Task.CompletedTask;
    }

    /// <summary>Unbooks a shipment. Stages the result; the caller commits.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CancelAsync(Guid orderId, SagaVariant variant, CancellationToken cancellationToken)
    {
        Shipment? shipment = await _dbContext.Shipments
            .FirstOrDefaultAsync(entity => entity.OrderId == orderId, cancellationToken);

        if (shipment is null)
        {
            return;
        }

        shipment.Cancel(_timeProvider.GetUtcNow());
        _outbox.Stage(new ShipmentCancelled(orderId, variant, shipment.Id));
    }
}
