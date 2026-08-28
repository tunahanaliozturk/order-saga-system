namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>
/// An outbound message staged in the same local transaction as the business change that produced it.
/// </summary>
/// <remarks>
/// <para>
/// This row is the answer to the dual-write problem. Writing to the database and then publishing to the
/// broker are two operations with no shared transaction, so a crash between them leaves the system
/// permanently inconsistent: the payment is authorized and nobody will ever be told to ship. Staging the
/// message as a row makes "change the state" and "tell the world" the same commit.
/// </para>
/// <para>
/// The relay publishes it afterwards, at least once, until the broker acknowledges. Duplicates are
/// expected and are handled on the consuming side by the idempotency ledger, not prevented here.
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        MessageType = null!;
        Payload = null!;
    }

    /// <summary>Message identifier. Carried onto the bus so consumers deduplicate on a stable value.</summary>
    /// <remarks>
    /// This has to be the message id the broker sees, not a fresh one per publish attempt. If the relay
    /// generated a new id on a retry, a redelivered message would look new to every consumer and the
    /// idempotency ledger would never match.
    /// </remarks>
    public Guid Id { get; private set; }

    /// <summary>Monotonic position assigned by the database. The relay publishes in this order.</summary>
    public long Sequence { get; private set; }

    /// <summary>The order this message belongs to. Also the trace and log correlation value.</summary>
    public Guid CorrelationId { get; private set; }

    /// <summary>Contract name, resolved back to a CLR type by the registry at publish time.</summary>
    public string MessageType { get; private set; }

    /// <summary>The serialised message.</summary>
    public string Payload { get; private set; }

    /// <summary>When the producing transaction committed.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When the broker acknowledged the publish. Null means still pending.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>How many publish attempts have been made.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>Why the last publish attempt failed.</summary>
    public string? LastError { get; private set; }

    /// <summary>Stages a message. Save it with the caller's business change, never on its own.</summary>
    /// <param name="correlationId">Order id.</param>
    /// <param name="messageType">Contract name.</param>
    /// <param name="payload">Serialised message.</param>
    /// <param name="occurredAt">Current time.</param>
    public static OutboxMessage Stage(
        Guid correlationId,
        string messageType,
        string payload,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        return new OutboxMessage
        {
            Id = Guid.CreateVersion7(occurredAt),
            CorrelationId = correlationId,
            MessageType = messageType,
            Payload = payload,
            OccurredAt = occurredAt,
        };
    }

    /// <summary>Marks the row published, after the broker has acknowledged and not before.</summary>
    /// <param name="publishedAt">Current time.</param>
    public void MarkPublished(DateTimeOffset publishedAt)
    {
        PublishedAt = publishedAt;
        AttemptCount++;
        LastError = null;
    }

    /// <summary>Records a failed publish attempt. The row stays pending and is picked up again.</summary>
    /// <param name="error">What went wrong.</param>
    public void RecordFailure(string error)
    {
        AttemptCount++;
        LastError = error.Length <= 1024 ? error : error[..1024];
    }
}
