using MassTransit;
using OrderSaga.BuildingBlocks.Diagnostics;
using OrderSaga.Contracts;

namespace OrderSaga.BuildingBlocks.Messaging;

/// <summary>
/// The shape every consumer in this system shares: apply once, stage what follows, ack either way.
/// </summary>
/// <remarks>
/// <para>
/// Twelve consumers need identical handling of three things that are easy to get subtly wrong: taking the
/// message id from the transport rather than the payload, committing the business change and the ledger
/// entry together, and acking a duplicate instead of letting it redeliver forever. Putting that in one
/// place means a new consumer gets it right by construction.
/// </para>
/// <para>
/// Both saga variants run on the same services and the same broker, so a consumer that only belongs to one
/// of them declares it. Without that, the choreographed refund subscriber would also react to failures the
/// orchestrator is already compensating, and the customer would be refunded twice.
/// </para>
/// </remarks>
/// <typeparam name="TMessage">Contract this consumer handles.</typeparam>
/// <param name="guard">Idempotency guard over the service's context.</param>
/// <param name="diagnostics">Metrics.</param>
public abstract class IdempotentConsumer<TMessage>(IdempotencyGuard guard, SagaDiagnostics diagnostics)
    : IConsumer<TMessage>
    where TMessage : class, ISagaMessage
{
    private readonly IdempotencyGuard _guard = guard ?? throw new ArgumentNullException(nameof(guard));

    /// <summary>Metrics, exposed so a compensating consumer can record what it undid.</summary>
    protected SagaDiagnostics Diagnostics { get; } =
        diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

    /// <summary>Identity written to the ledger. Stable, because changing it replays every past message.</summary>
    protected abstract string ConsumerName { get; }

    /// <summary>
    /// Restricts this consumer to one coordination strategy, or null to handle both.
    /// </summary>
    protected virtual SagaVariant? RestrictedTo => null;

    /// <inheritdoc />
    public async Task Consume(ConsumeContext<TMessage> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (RestrictedTo is { } variant && context.Message.Variant != variant)
        {
            // Not this consumer's flow. Acked, because the message is legitimately someone else's.
            return;
        }

        // The id comes from the transport, not the payload: it is what stays stable across a redelivery,
        // and it is what the relay set from the outbox row id.
        Guid messageId = context.MessageId
            ?? throw new InvalidOperationException(
                $"{ConsumerName} received a {typeof(TMessage).Name} with no message id, so it cannot be deduplicated.");

        using var activity = SagaDiagnostics.ActivitySource.StartActivity(
            $"consume {typeof(TMessage).Name}",
            System.Diagnostics.ActivityKind.Consumer);

        activity?.SetTag("saga.correlation_id", context.Message.CorrelationId);
        activity?.SetTag("saga.variant", context.Message.Variant.ToString());
        activity?.SetTag("messaging.consumer.name", ConsumerName);

        ConsumeOutcome outcome = await _guard.ExecuteOnceAsync(
            ConsumerName,
            messageId,
            token => HandleAsync(context.Message, token),
            context.CancellationToken);

        activity?.SetTag("saga.consume_outcome", outcome.ToString());

        if (outcome is ConsumeOutcome.Duplicate)
        {
            Diagnostics.DuplicatesIgnored.Add(
                1,
                new KeyValuePair<string, object?>("messaging.consumer.name", ConsumerName));
        }
    }

    /// <summary>
    /// Stages the business change and any outbound messages.
    /// </summary>
    /// <remarks>
    /// Must not save. The guard owns the commit so that the business row, the outbox rows, and the ledger
    /// entry land in one transaction, which is the only arrangement that makes the effect happen once.
    /// </remarks>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}
