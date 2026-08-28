using Microsoft.EntityFrameworkCore;

namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>
/// The two tables every service in this system needs, whatever else it owns.
/// </summary>
/// <remarks>
/// Services do not share a schema and never read each other's tables. What they do share is the shape of
/// the two mechanisms that make messaging safe: an outbox on the way out and an idempotency ledger on the
/// way in. Both live in the service's own database, next to its business tables, because that is the only
/// way they can take part in the same local transaction.
/// </remarks>
/// <param name="options">Provider options.</param>
public abstract class ServiceDbContext(DbContextOptions options) : DbContext(options)
{
    /// <summary>Messages staged for publication.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Messages this service has already applied.</summary>
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(message => message.Id);

            outbox.Property(message => message.Sequence).UseIdentityAlwaysColumn().ValueGeneratedOnAdd();
            outbox.Property(message => message.MessageType).HasMaxLength(256).IsRequired();
            outbox.Property(message => message.Payload).HasColumnType("jsonb").IsRequired();
            outbox.Property(message => message.LastError).HasMaxLength(1024);

            // The relay's whole query is a range scan on this index: pending rows, in commit order. It is
            // partial because a published row is never a candidate again, and published rows are almost
            // all of the table.
            outbox.HasIndex(message => message.Sequence)
                .HasDatabaseName("ix_outbox_messages_pending")
                .HasFilter("published_at IS NULL");

            // Retention sweeps by publication time.
            outbox.HasIndex(message => message.PublishedAt).HasDatabaseName("ix_outbox_messages_published");
        });

        modelBuilder.Entity<ProcessedMessage>(processed =>
        {
            processed.ToTable("processed_messages");

            // The composite key is the idempotency mechanism. Nothing in application code decides whether
            // a message is a duplicate; the database does, by refusing the second insert.
            processed.HasKey(message => new { message.ConsumerName, message.MessageId });

            processed.Property(message => message.ConsumerName).HasMaxLength(128);

            // Retention sweeps by processing time, and must outlive the broker's redelivery window.
            processed.HasIndex(message => message.ProcessedAt).HasDatabaseName("ix_processed_messages_age");
        });
    }
}
