using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>What happened when a consumer tried to apply a message.</summary>
public enum ConsumeOutcome
{
    /// <summary>The message was applied and the transaction committed.</summary>
    Applied = 0,

    /// <summary>The message had already been applied. Nothing changed, and the broker should still be acked.</summary>
    Duplicate = 1,
}

/// <summary>
/// Makes a consumer's side effects happen exactly once, however many times the broker delivers.
/// </summary>
/// <remarks>
/// <para>
/// The business write, the outbox row it produces, and the ledger entry saying "this message is done" all
/// go in one <c>SaveChangesAsync</c>. That is what makes the guarantee hold: there is no interval in which
/// the effect exists but the ledger entry does not, so a crash cannot produce a second charge on restart.
/// </para>
/// <para>
/// Duplicates are detected by the unique constraint, not by a preceding read. A read-then-write would let
/// two concurrent redeliveries both find nothing and both proceed, which is the exact race the ledger is
/// supposed to close.
/// </para>
/// </remarks>
/// <param name="dbContext">The service's context.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class IdempotencyGuard(
    ServiceDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<IdempotencyGuard> logger)
{
    private const string UniqueViolation = "23505";

    private readonly ServiceDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<IdempotencyGuard> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Runs a consumer's work once per message id.
    /// </summary>
    /// <param name="consumerName">Consumer identity. Two consumers may legitimately handle the same message.</param>
    /// <param name="messageId">The broker's message id, stable across redeliveries.</param>
    /// <param name="work">
    /// Stages the business change and any outbound messages. It must not call <c>SaveChangesAsync</c>
    /// itself: this method owns the commit, and that is the whole point.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ConsumeOutcome> ExecuteOnceAsync(
        string consumerName,
        Guid messageId,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        ArgumentNullException.ThrowIfNull(work);

        if (messageId == Guid.Empty)
        {
            throw new ArgumentException(
                "The message has no id, so it cannot be deduplicated.",
                nameof(messageId));
        }

        _dbContext.ProcessedMessages.Add(
            ProcessedMessage.Create(consumerName, messageId, _timeProvider.GetUtcNow()));

        await work(cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return ConsumeOutcome.Applied;
        }
        catch (DbUpdateException exception) when (IsDuplicate(exception))
        {
            // Expected under at-least-once delivery, so it is logged as an outcome rather than an error,
            // and the message is still acked. Rethrowing would make the broker redeliver forever.
            LogDuplicateIgnored(_logger, consumerName, messageId);

            _dbContext.ChangeTracker.Clear();
            return ConsumeOutcome.Duplicate;
        }
    }

    private static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: UniqueViolation };

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "DuplicateMessageIgnored: {ConsumerName} has already applied message {MessageId}.")]
    private static partial void LogDuplicateIgnored(ILogger logger, string consumerName, Guid messageId);
}
