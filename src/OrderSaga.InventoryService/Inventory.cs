using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OrderSaga.BuildingBlocks.Messaging;

namespace OrderSaga.InventoryService;

/// <summary>How much of one product is on hand.</summary>
public sealed class StockItem
{
    private StockItem()
    {
    }

    /// <summary>Product identifier.</summary>
    public Guid Sku { get; private set; }

    /// <summary>Units not currently held by a reservation.</summary>
    public int Available { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    /// <remarks>
    /// Two orders for the last unit of the same product will race. Without this, both reads see one
    /// available, both write zero, and two customers are promised the same item. With it, the second
    /// write fails, the message is retried, and the second order correctly finds nothing left.
    /// </remarks>
    public uint Version { get; private set; }

    /// <summary>Creates a stock row.</summary>
    /// <param name="sku">Product.</param>
    /// <param name="available">Units on hand.</param>
    public static StockItem Create(Guid sku, int available)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(available);
        return new StockItem { Sku = sku, Available = available };
    }

    /// <summary>Takes units off the shelf, if there are enough.</summary>
    /// <param name="quantity">How many.</param>
    public bool TryTake(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        if (Available < quantity)
        {
            return false;
        }

        Available -= quantity;
        return true;
    }

    /// <summary>Puts units back.</summary>
    /// <param name="quantity">How many.</param>
    public void Restore(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        Available += quantity;
    }

    /// <summary>Overwrites the count. Used by the seeding endpoint and by tests.</summary>
    /// <param name="available">Units on hand.</param>
    public void SetAvailable(int available)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(available);
        Available = available;
    }
}

/// <summary>Where a hold stands.</summary>
public enum ReservationStatus
{
    /// <summary>Stock is set aside for this order.</summary>
    Held = 0,

    /// <summary>The hold was released by a compensation.</summary>
    Released = 1,
}

/// <summary>Stock held for one order.</summary>
public sealed class Reservation
{
    private readonly List<ReservationLine> _lines = [];

    private Reservation()
    {
    }

    /// <summary>Identifier. Handed to the release compensation.</summary>
    public Guid Id { get; private set; }

    /// <summary>The order this belongs to.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>Current state.</summary>
    public ReservationStatus Status { get; private set; }

    /// <summary>What is held.</summary>
    public IReadOnlyList<ReservationLine> Lines => _lines;

    /// <summary>When the hold was taken.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it was released.</summary>
    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>Creates a hold.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="lines">What is held.</param>
    /// <param name="now">Current time.</param>
    public static Reservation Hold(
        Guid orderId,
        IEnumerable<(Guid Sku, int Quantity)> lines,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            Status = ReservationStatus.Held,
            CreatedAt = now,
        };

        foreach ((Guid sku, int quantity) in lines)
        {
            reservation._lines.Add(new ReservationLine
            {
                Id = Guid.CreateVersion7(now),
                ReservationId = reservation.Id,
                Sku = sku,
                Quantity = quantity,
            });
        }

        return reservation;
    }

    /// <summary>Releases the hold. Safe to call on an already released reservation.</summary>
    /// <param name="now">Current time.</param>
    /// <returns>True if this call performed the release.</returns>
    public bool Release(DateTimeOffset now)
    {
        if (Status is not ReservationStatus.Held)
        {
            return false;
        }

        Status = ReservationStatus.Released;
        ReleasedAt = now;
        return true;
    }
}

/// <summary>One product held by a reservation.</summary>
public sealed class ReservationLine
{
    /// <summary>Identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning reservation.</summary>
    public Guid ReservationId { get; init; }

    /// <summary>Product.</summary>
    public Guid Sku { get; init; }

    /// <summary>Units held.</summary>
    public int Quantity { get; init; }
}

/// <summary>
/// Order lines this service was told about, kept because a later event will not repeat them.
/// </summary>
/// <remarks>
/// This is the cost of choreography made concrete. <c>PaymentAuthorized</c> does not carry order lines, and
/// database-per-service means Inventory cannot go and read them from the Order service. So every
/// participant has to persist whatever it will need later, in its own database, from an event it might
/// otherwise have ignored. The orchestrated flow needs none of this: the state machine already knows.
/// </remarks>
public sealed class KnownOrder
{
    private KnownOrder() => LinesJson = null!;

    /// <summary>Order.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>The lines, as they arrived.</summary>
    public string LinesJson { get; private set; }

    /// <summary>When this service first heard about the order.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>Records what an order contains.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="linesJson">Serialised lines.</param>
    /// <param name="recordedAt">Current time.</param>
    public static KnownOrder Create(Guid orderId, string linesJson, DateTimeOffset recordedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linesJson);

        return new KnownOrder
        {
            OrderId = orderId,
            LinesJson = linesJson,
            RecordedAt = recordedAt,
        };
    }
}

/// <summary>The Inventory service's own database.</summary>
/// <param name="options">Provider options.</param>
public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options)
    : ServiceDbContext(options)
{
    /// <summary>Stock on hand.</summary>
    public DbSet<StockItem> Stock => Set<StockItem>();

    /// <summary>Holds taken for orders.</summary>
    public DbSet<Reservation> Reservations => Set<Reservation>();

    /// <summary>Order contents this service remembered from OrderCreated.</summary>
    public DbSet<KnownOrder> KnownOrders => Set<KnownOrder>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<StockItem>(stock =>
        {
            stock.ToTable("stock_items");
            stock.HasKey(item => item.Sku);
            stock.Property(item => item.Version).IsRowVersion();
        });

        modelBuilder.Entity<Reservation>(reservation =>
        {
            reservation.ToTable("reservations");
            reservation.HasKey(entity => entity.Id);
            reservation.Property(entity => entity.Status).HasConversion<int>();

            // One reservation per order, enforced by the database, behind the idempotency ledger.
            reservation.HasIndex(entity => entity.OrderId).IsUnique();

            reservation.HasMany(entity => entity.Lines)
                .WithOne()
                .HasForeignKey(line => line.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);

            reservation.Navigation(entity => entity.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<KnownOrder>(known =>
        {
            known.ToTable("known_orders");
            known.HasKey(entity => entity.OrderId);
            known.Property(entity => entity.LinesJson).HasColumnType("jsonb").IsRequired();
        });

        modelBuilder.Entity<ReservationLine>(line =>
        {
            line.ToTable("reservation_lines");
            line.HasKey(entity => entity.Id);
        });
    }
}

/// <summary>Lets the EF tooling build a context without booting the service.</summary>
public sealed class InventoryDbContextFactory : IDesignTimeDbContextFactory<InventoryDbContext>
{
    /// <inheritdoc />
    public InventoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql("Host=localhost;Database=inventorydb;Username=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
