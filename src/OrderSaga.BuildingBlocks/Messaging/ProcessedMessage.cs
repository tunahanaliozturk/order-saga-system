namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>
/// One row per message a consumer has already applied.
/// </summary>
/// <remarks>
/// <para>
/// The broker guarantees at-least-once delivery, so every consumer will eventually see the same message
/// twice: a redelivery timeout, a crash between the business write and the ack, a consumer-group
/// rebalance. Without this ledger, that means a second charge on a customer's card.
/// </para>
/// <para>
/// The mechanism is the unique constraint on <c>(consumer_name, message_id)</c>, not application code. A
/// check-then-write would still let two concurrent redeliveries both pass the check. Insert, let the
/// database reject the second one, and treat the rejection as "already done".
/// </para>
/// </remarks>
public sealed class ProcessedMessage
{
    private ProcessedMessage() => ConsumerName = null!;

    /// <summary>Which consumer applied it. Two consumers may legitimately process the same message.</summary>
    public string ConsumerName { get; private set; }

    /// <summary>The message id, stable across redeliveries.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>When it was applied.</summary>
    public DateTimeOffset ProcessedAt { get; private set; }

    /// <summary>Records that a consumer has applied a message.</summary>
    /// <param name="consumerName">Consumer identity.</param>
    /// <param name="messageId">Message id.</param>
    /// <param name="processedAt">Current time.</param>
    public static ProcessedMessage Create(string consumerName, Guid messageId, DateTimeOffset processedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        return new ProcessedMessage
        {
            ConsumerName = consumerName,
            MessageId = messageId,
            ProcessedAt = processedAt,
        };
    }
}
