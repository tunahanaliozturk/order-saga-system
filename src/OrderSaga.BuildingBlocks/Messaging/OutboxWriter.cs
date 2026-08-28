using OrderSaga.Contracts;

namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>Stages an outbound message inside the caller's existing unit of work.</summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Adds the message to the outbox. It goes out when the caller saves, and not before.
    /// </summary>
    /// <remarks>
    /// Deliberately not async and deliberately does not save. Saving here would open a second transaction
    /// and reintroduce exactly the dual write this table exists to remove.
    /// </remarks>
    /// <param name="message">The message to publish once the transaction commits.</param>
    /// <typeparam name="TMessage">Contract type.</typeparam>
    OutboxMessage Stage<TMessage>(TMessage message)
        where TMessage : ISagaMessage;
}

/// <summary>Writes outbox rows through the service's own <see cref="ServiceDbContext"/>.</summary>
/// <param name="dbContext">The context the caller's business write is already using.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class OutboxWriter(ServiceDbContext dbContext, TimeProvider timeProvider) : IOutboxWriter
{
    private readonly ServiceDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public OutboxMessage Stage<TMessage>(TMessage message)
        where TMessage : ISagaMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        OutboxMessage row = OutboxMessage.Stage(
            message.CorrelationId,
            MessageTypeRegistry.NameOf(message.GetType()),
            MessageTypeRegistry.Serialize(message),
            _timeProvider.GetUtcNow());

        _dbContext.OutboxMessages.Add(row);
        return row;
    }
}
