using System.Globalization;

namespace OrderSaga.BuildingBlocks.Faults;

/// <summary>The kind of failure the injector produces.</summary>
public enum FaultMode
{
    /// <summary>Business rejection. A legitimate outcome that triggers compensation, not an error.</summary>
    Decline = 0,

    /// <summary>The processor never answered. Throws, so the broker redelivers.</summary>
    Timeout = 1,

    /// <summary>The connection dropped mid-call. Throws, so the broker redelivers.</summary>
    ConnectionReset = 2,
}

/// <summary>
/// Makes a service fail on demand, so failure paths are exercised rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a runtime dial rather than a compile-time flag. Branching production code on a "test mode"
/// switch means the code that runs in tests is not the code that runs in production, which is exactly the
/// arrangement that lets a compensation path rot unnoticed.
/// </para>
/// <para>
/// The distinction between the modes is the whole point. A decline is a business outcome the saga
/// compensates for; a timeout or a reset connection is a transport failure the broker retries. Both need
/// to work, and they need to work differently.
/// </para>
/// </remarks>
public sealed class FaultInjector
{
    private readonly Lock _gate = new();
    private double _rate;
    private FaultMode _mode = FaultMode.Decline;

    /// <summary>Probability in [0, 1] that the next operation fails.</summary>
    public double Rate
    {
        get { lock (_gate) { return _rate; } }
    }

    /// <summary>How it fails.</summary>
    public FaultMode Mode
    {
        get { lock (_gate) { return _mode; } }
    }

    /// <summary>Sets the dial.</summary>
    /// <param name="rate">Probability in [0, 1].</param>
    /// <param name="mode">How to fail.</param>
    public void Configure(double rate, FaultMode mode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1d);

        lock (_gate)
        {
            _rate = rate;
            _mode = mode;
        }
    }

    /// <summary>Turns injection off.</summary>
    public void Reset() => Configure(0, FaultMode.Decline);

    /// <summary>
    /// Decides whether this operation fails, and throws for the transport-level modes.
    /// </summary>
    /// <param name="operation">Named in the exception so a test failure says which call blew up.</param>
    /// <returns>True when the caller should treat the operation as a business rejection.</returns>
    public bool ShouldDecline(string operation)
    {
        double rate;
        FaultMode mode;

        lock (_gate)
        {
            rate = _rate;
            mode = _mode;
        }

        if (rate <= 0 || Random.Shared.NextDouble() >= rate)
        {
            return false;
        }

        return mode switch
        {
            FaultMode.Decline => true,
            FaultMode.Timeout => throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"Injected timeout during {operation}.")),
            FaultMode.ConnectionReset => throw new IOException(
                string.Create(CultureInfo.InvariantCulture, $"Injected connection reset during {operation}.")),
            _ => false,
        };
    }
}

/// <summary>Request body for the fault-injection endpoint.</summary>
/// <param name="Rate">Probability in [0, 1].</param>
/// <param name="Mode">How to fail.</param>
public sealed record FaultRateRequest(double Rate, FaultMode Mode);
