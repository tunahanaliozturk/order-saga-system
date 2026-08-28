using Microsoft.EntityFrameworkCore;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.PaymentService;

/// <summary>
/// What the Payment service actually does, independent of how it was asked.
/// </summary>
/// <remarks>
/// Orchestration sends a command and choreography publishes an event, but the work is identical, so it
/// lives here and both consumers call it. Duplicating it per variant is how the two flows would quietly
/// diverge, which would make the whole "compare the two strategies" claim meaningless.
/// </remarks>
/// <param name="dbContext">The service's context.</param>
/// <param name="outbox">Stages outbound messages in the caller's transaction.</param>
/// <param name="faults">Fault dial.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class PaymentProcessor(
    PaymentDbContext dbContext,
    IOutboxWriter outbox,
    FaultInjector faults,
    TimeProvider timeProvider)
{
    private readonly PaymentDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IOutboxWriter _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

    private readonly FaultInjector _faults = faults ?? throw new ArgumentNullException(nameof(faults));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Holds funds, or declines. Stages the resulting event; the caller commits.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="customerId">Customer.</param>
    /// <param name="amount">Amount.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AuthorizeAsync(
        Guid orderId,
        Guid customerId,
        decimal amount,
        SagaVariant variant,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        // Throws for the transport-level fault modes, which is what makes the broker redeliver.
        if (_faults.ShouldDecline(nameof(AuthorizeAsync)))
        {
            const string Reason = "Card issuer declined the authorization.";

            _dbContext.Payments.Add(Payment.Decline(orderId, customerId, amount, Reason, now));
            _outbox.Stage(new PaymentDeclined(orderId, variant, Reason));
            return;
        }

        Payment payment = Payment.Authorize(orderId, customerId, amount, now);

        _dbContext.Payments.Add(payment);
        _outbox.Stage(new PaymentAuthorized(orderId, variant, payment.Id, amount));
    }

    /// <summary>
    /// Releases a hold. Stages the resulting event; the caller commits.
    /// </summary>
    /// <remarks>
    /// A refund for an order with no authorized payment is not an error. It happens when the payment was
    /// declined and something downstream compensated anyway, and the honest response is to confirm the
    /// end state rather than to fail and have the broker redeliver a refund that can never succeed.
    /// </remarks>
    /// <param name="orderId">Order.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RefundAsync(Guid orderId, SagaVariant variant, CancellationToken cancellationToken)
    {
        Payment? payment = await _dbContext.Payments
            .FirstOrDefaultAsync(entity => entity.OrderId == orderId, cancellationToken);

        if (payment is null)
        {
            return;
        }

        payment.Refund(_timeProvider.GetUtcNow());
        _outbox.Stage(new PaymentRefunded(orderId, variant, payment.Id));
    }
}
