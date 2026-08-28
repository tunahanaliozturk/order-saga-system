using System.Diagnostics;
using System.Diagnostics.Metrics;
using OrderSaga.Contracts;

namespace OrderSaga.BuildingBlocks.Diagnostics;

/// <summary>
/// Tracing and metrics for the order flow.
/// </summary>
/// <remarks>
/// The instruments answer the three questions this system gets asked in an incident: are orders finishing,
/// how long are they taking, and is the relay keeping up. Everything is tagged with the saga variant, so
/// the orchestration and choreography numbers can be compared rather than argued about.
/// </remarks>
public sealed class SagaDiagnostics : IDisposable
{
    /// <summary>Name used for both the activity source and the meter.</summary>
    public const string SourceName = "OrderSaga";

    private readonly Meter _meter;

    /// <summary>Creates the instruments.</summary>
    /// <param name="meterFactory">Factory, so tests get an isolated meter.</param>
    public SagaDiagnostics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(SourceName);

        SagasCompleted = _meter.CreateCounter<long>(
            "ordersaga.saga.completed",
            unit: "{saga}",
            description: "Sagas that reached a terminal state, tagged by variant and outcome.");

        SagaDuration = _meter.CreateHistogram<double>(
            "ordersaga.saga.duration",
            unit: "ms",
            description: "Time from order creation to a terminal state.");

        CompensationsFired = _meter.CreateCounter<long>(
            "ordersaga.compensation.fired",
            unit: "{compensation}",
            description: "Compensating actions dispatched, tagged by step.");

        DuplicatesIgnored = _meter.CreateCounter<long>(
            "ordersaga.idempotency.duplicate",
            unit: "{message}",
            description: "Redeliveries the idempotency ledger absorbed.");

        RelayLag = _meter.CreateHistogram<double>(
            "ordersaga.outbox.relay.lag",
            unit: "ms",
            description: "Time from local commit to broker publish acknowledgement.");
    }

    /// <summary>Spans for order creation, saga transitions, and each consumer.</summary>
    public static ActivitySource ActivitySource { get; } = new(SourceName);

    /// <summary>Sagas that reached a terminal state.</summary>
    public Counter<long> SagasCompleted { get; }

    /// <summary>End-to-end saga duration in milliseconds.</summary>
    public Histogram<double> SagaDuration { get; }

    /// <summary>Compensating actions dispatched.</summary>
    public Counter<long> CompensationsFired { get; }

    /// <summary>Redeliveries absorbed by the ledger.</summary>
    public Counter<long> DuplicatesIgnored { get; }

    /// <summary>Outbox relay lag in milliseconds.</summary>
    public Histogram<double> RelayLag { get; }

    /// <summary>Records a saga reaching a terminal state.</summary>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="outcome">Terminal state name.</param>
    /// <param name="duration">How long it took end to end.</param>
    public void RecordSagaCompleted(SagaVariant variant, string outcome, TimeSpan duration)
    {
        var variantTag = new KeyValuePair<string, object?>("saga.variant", variant.ToString());
        var outcomeTag = new KeyValuePair<string, object?>("saga.outcome", outcome);

        SagasCompleted.Add(1, variantTag, outcomeTag);
        SagaDuration.Record(duration.TotalMilliseconds, variantTag, outcomeTag);
    }

    /// <summary>Records one compensating action.</summary>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="step">Which step is being undone.</param>
    public void RecordCompensation(SagaVariant variant, string step) =>
        CompensationsFired.Add(
            1,
            new KeyValuePair<string, object?>("saga.variant", variant.ToString()),
            new KeyValuePair<string, object?>("saga.step", step));

    /// <summary>Records how long a message waited between commit and publish.</summary>
    /// <param name="lag">The gap.</param>
    public void RecordRelayLag(TimeSpan lag) => RelayLag.Record(Math.Max(lag.TotalMilliseconds, 0));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
