using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderSaga.OrderService;

/// <summary>Stuck-order detection settings.</summary>
public sealed class StuckOrderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OrderSaga:StuckOrders";

    /// <summary>How long an order may run before it is considered stuck.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "24:00:00")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to look. Zero turns the sweep off.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Finds orders that stopped making progress, so nobody has to notice by hand.
/// </summary>
/// <remarks>
/// <para>
/// Eventual consistency is only an acceptable model if "eventually" has a bound. Without this, an order
/// whose next message was lost sits in a non-terminal state indefinitely and the system reports nothing
/// wrong. Flagging it converts silent inconsistency into something alertable and, through the retry
/// endpoint, fixable.
/// </para>
/// <para>
/// The age is measured from the order's own creation timestamp against one clock, not by subtracting
/// timestamps written by four different containers. Otherwise the timeout would be a function of clock
/// skew rather than of elapsed time.
/// </para>
/// </remarks>
/// <param name="scopeFactory">Scope factory.</param>
/// <param name="options">Timeout settings.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
public sealed partial class StuckOrderSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<StuckOrderOptions> options,
    TimeProvider timeProvider,
    ILogger<StuckOrderSweeper> logger) : BackgroundService
{
    private readonly StuckOrderOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.SweepInterval <= TimeSpan.Zero)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A sweep that dies takes the alerting with it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogSweepFailed(logger, exception);
            }

            await Task.Delay(_options.SweepInterval, timeProvider, stoppingToken);
        }
    }

    /// <summary>Flags overdue orders. Exposed so tests can run it on demand.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many orders were newly flagged.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        OrderDbContext dbContext = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - _options.Timeout;

        List<Order> overdue = await dbContext.Orders
            .Where(order => !order.IsStuck
                && order.CreatedAt < cutoff
                && order.Status != OrderStatus.Completed
                && order.Status != OrderStatus.Cancelled)
            .ToListAsync(cancellationToken);

        foreach (Order order in overdue)
        {
            order.FlagStuck(now);
            LogStuck(logger, order.Id, order.Status.ToString(), now - order.CreatedAt);
        }

        if (overdue.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return overdue.Count;
    }

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Warning,
        Message = "Order {OrderId} has been in {Status} for {Age} without reaching a terminal state.")]
    private static partial void LogStuck(ILogger logger, Guid orderId, string status, TimeSpan age);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Error,
        Message = "Stuck-order sweep failed. Retrying on the next interval.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
