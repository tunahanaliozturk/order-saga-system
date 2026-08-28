using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OrderSaga.BuildingBlocks.Messaging;

namespace OrderSaga.PaymentService;

/// <summary>The Payment service's own database. Nothing outside this service reads it.</summary>
/// <param name="options">Provider options.</param>
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : ServiceDbContext(options)
{
    /// <summary>Authorization attempts.</summary>
    public DbSet<Payment> Payments => Set<Payment>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.ToTable("payments");
            payment.HasKey(entity => entity.Id);

            payment.Property(entity => entity.Amount).HasPrecision(18, 2);
            payment.Property(entity => entity.Status).HasConversion<int>();
            payment.Property(entity => entity.DeclineReason).HasMaxLength(256);

            // One authorization per order, enforced by the database. The idempotency ledger already
            // prevents a duplicate message from getting this far; this is what stops a second charge if
            // anything ever gets past it.
            payment.HasIndex(entity => entity.OrderId).IsUnique();
        });
    }
}

/// <summary>Lets the EF tooling build a context without booting the service.</summary>
public sealed class PaymentDbContextFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    /// <inheritdoc />
    public PaymentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql("Host=localhost;Database=paymentdb;Username=postgres")
            .UseSnakeCaseNamingConvention()
            .Options);
}
