namespace OrderSaga.PaymentService;

/// <summary>Where an authorization stands.</summary>
public enum PaymentStatus
{
    /// <summary>Funds are held.</summary>
    Authorized = 0,

    /// <summary>The processor said no. Nothing is held.</summary>
    Declined = 1,

    /// <summary>The hold was released by a compensation.</summary>
    Refunded = 2,
}

/// <summary>One authorization attempt against one order.</summary>
/// <remarks>
/// The unique index on the order id is a second line of defence behind the idempotency ledger. If the
/// ledger were ever bypassed, the database would still refuse to record a second authorization for the
/// same order rather than quietly charging the customer twice.
/// </remarks>
public sealed class Payment
{
    private Payment() => DeclineReason = null;

    /// <summary>Identifier. Handed to the refund compensation.</summary>
    public Guid Id { get; private set; }

    /// <summary>The order this belongs to.</summary>
    public Guid OrderId { get; private set; }

    /// <summary>Who is being charged.</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>How much.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Current state.</summary>
    public PaymentStatus Status { get; private set; }

    /// <summary>Why the processor declined, when it did.</summary>
    public string? DeclineReason { get; private set; }

    /// <summary>When the attempt was made.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the hold was released.</summary>
    public DateTimeOffset? RefundedAt { get; private set; }

    /// <summary>Records a successful hold.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="customerId">Customer.</param>
    /// <param name="amount">Amount held.</param>
    /// <param name="now">Current time.</param>
    public static Payment Authorize(Guid orderId, Guid customerId, decimal amount, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        return new Payment
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Status = PaymentStatus.Authorized,
            CreatedAt = now,
        };
    }

    /// <summary>Records a decline. Kept as a row so the timeline can explain why the order was cancelled.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="customerId">Customer.</param>
    /// <param name="amount">Amount attempted.</param>
    /// <param name="reason">What the processor said.</param>
    /// <param name="now">Current time.</param>
    public static Payment Decline(
        Guid orderId,
        Guid customerId,
        decimal amount,
        string reason,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            OrderId = orderId,
            CustomerId = customerId,
            Amount = amount,
            Status = PaymentStatus.Declined,
            DeclineReason = reason,
            CreatedAt = now,
        };

    /// <summary>
    /// Releases the hold. Safe to call on an already refunded payment.
    /// </summary>
    /// <remarks>
    /// A compensation is a message like any other, so it arrives at least once. Making the transition
    /// itself tolerant of repetition means a duplicate that somehow gets past the ledger still cannot
    /// produce a second refund.
    /// </remarks>
    /// <param name="now">Current time.</param>
    /// <returns>True if this call performed the refund.</returns>
    public bool Refund(DateTimeOffset now)
    {
        if (Status is not PaymentStatus.Authorized)
        {
            return false;
        }

        Status = PaymentStatus.Refunded;
        RefundedAt = now;
        return true;
    }
}
