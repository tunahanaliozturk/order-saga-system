using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderSaga.BuildingBlocks.Diagnostics;

namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>Tuning for the relay and the retention sweeps.</summary>
public sealed class OutboxOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OrderSaga:Outbox";

    /// <summary>How long to sleep after a poll that found nothing. A poll that found work loops straight round.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:01:00")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Rows claimed per poll.</summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 100;

    /// <summary>How long published rows are kept before the sweep removes them.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "3650.00:00:00")]
    public TimeSpan PublishedRetention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long ledger entries are kept.
    /// </summary>
    /// <remarks>
    /// This must comfortably outlive the broker's maximum redelivery window. Purge an entry while the
    /// broker can still redeliver its message and the effect happens a second time, which is the one
    /// failure the ledger exists to prevent. The coupling is deliberate and is called out in the runbook.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "3650.00:00:00")]
    public TimeSpan ProcessedRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often the retention sweep runs. Zero turns it off.</summary>
    public TimeSpan RetentionSweepInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Publishes what the business transaction staged, at least once, until the broker acknowledges.
/// </summary>
/// <remarks>
/// <para>
/// Claims rows with <c>FOR UPDATE SKIP LOCKED</c> so several instances of a service can run the relay
/// without publishing the same row twice. Rows are marked published only after the broker acknowledges,
/// which means a crash in between republishes rather than loses. That is the trade this design makes on
/// purpose: duplicates are cheap because every consumer is idempotent, and a lost message is not.
/// </para>
/// <para>
/// The outbox row id becomes the message id on the bus. If the relay let MassTransit generate a fresh id
/// per publish, a republished message would look new to every consumer and the idempotency ledger would
/// never match it.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Scope factory, since the context is scoped and the relay is not.</param>
/// <param name="options">Relay settings.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class OutboxRelay(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    private readonly OutboxOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<OutboxRelay> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset nextSweep = _timeProvider.GetUtcNow() + _options.RetentionSweepInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            int published;
            try
            {
                published = await RunOnceAsync(stoppingToken);

                if (_options.RetentionSweepInterval > TimeSpan.Zero && _timeProvider.GetUtcNow() >= nextSweep)
                {
                    await PurgeExpiredAsync(stoppingToken);
                    nextSweep = _timeProvider.GetUtcNow() + _options.RetentionSweepInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A relay that dies on one bad batch stops every outbound message.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogPassFailed(_logger, exception);
                published = 0;
            }

            if (published == 0)
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>Runs one publish pass. Exposed so tests can drive the relay deterministically.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many rows were published.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ServiceDbContext dbContext = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        IPublishEndpoint publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        MessageTypeRegistry registry = scope.ServiceProvider.GetRequiredService<MessageTypeRegistry>();
        SagaDiagnostics diagnostics = scope.ServiceProvider.GetRequiredService<SagaDiagnostics>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        List<OutboxMessage> claimed = await ClaimAsync(dbContext, cancellationToken);
        if (claimed.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        int published = 0;

        foreach (OutboxMessage row in claimed)
        {
            if (!registry.TryResolve(row.MessageType, out Type? messageType))
            {
                // A row this build cannot deserialise. Leaving it pending would block the queue forever,
                // so it is recorded and skipped, and the row shows up in the stuck-outbox metric.
                row.RecordFailure($"No contract type is registered for '{row.MessageType}'.");
                LogUnknownContract(_logger, row.Id, row.MessageType);
                continue;
            }

            try
            {
                object message = MessageTypeRegistry.Deserialize(messageType, row.Payload);

                await publisher.Publish(
                    message,
                    messageType,
                    Pipe.Execute<PublishContext>(context =>
                    {
                        context.MessageId = row.Id;
                        context.CorrelationId = row.CorrelationId;
                    }),
                    cancellationToken);

                DateTimeOffset now = _timeProvider.GetUtcNow();
                row.MarkPublished(now);
                diagnostics.RecordRelayLag(now - row.OccurredAt);
                published++;
            }
#pragma warning disable CA1031 // One unpublishable row must not strand the rest of the batch.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                row.RecordFailure(exception.Message);
                LogPublishFailed(_logger, row.Id, row.MessageType, exception);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return published;
    }

    /// <summary>Removes rows past their retention window. Exposed for tests.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        ServiceDbContext dbContext = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset outboxCutoff = now - _options.PublishedRetention;
        DateTimeOffset ledgerCutoff = now - _options.ProcessedRetention;

        int outbox = await dbContext.OutboxMessages
            .Where(message => message.PublishedAt != null && message.PublishedAt < outboxCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        int ledger = await dbContext.ProcessedMessages
            .Where(message => message.ProcessedAt < ledgerCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (outbox + ledger > 0)
        {
            LogPurged(_logger, outbox, ledger);
        }
    }

    private Task<List<OutboxMessage>> ClaimAsync(
        ServiceDbContext dbContext,
        CancellationToken cancellationToken)
    {
        int batchSize = _options.BatchSize;

        // Not composed over with LINQ on purpose: EF would wrap this in a subquery and Postgres rejects
        // FOR UPDATE there.
        return dbContext.OutboxMessages
            .FromSql(
                $"""
                 SELECT o.*
                 FROM outbox_messages AS o
                 WHERE o.published_at IS NULL
                 ORDER BY o.sequence
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Error,
        Message = "Outbox publish pass failed. Retrying on the next poll.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Error,
        Message = "Could not publish outbox row {MessageId} of type {MessageType}. It stays pending.")]
    private static partial void LogPublishFailed(
        ILogger logger,
        Guid messageId,
        string messageType,
        Exception exception);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Error,
        Message = "Outbox row {MessageId} names contract {MessageType}, which this build does not know.")]
    private static partial void LogUnknownContract(ILogger logger, Guid messageId, string messageType);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Information,
        Message = "Retention sweep removed {OutboxRows} outbox rows and {LedgerRows} ledger entries.")]
    private static partial void LogPurged(ILogger logger, int outboxRows, int ledgerRows);
}
