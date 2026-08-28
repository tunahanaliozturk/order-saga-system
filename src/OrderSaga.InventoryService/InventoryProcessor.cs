using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderSaga.BuildingBlocks.Faults;
using OrderSaga.BuildingBlocks.Messaging;
using OrderSaga.Contracts;

namespace OrderSaga.InventoryService;

/// <summary>Request body for the stock-seeding endpoint.</summary>
/// <param name="Quantity">Units on hand.</param>
public sealed record SetStockRequest(int Quantity);

/// <summary>Inventory settings.</summary>
public sealed class InventoryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "OrderSaga:Inventory";

    /// <summary>
    /// Stock assumed for a product nobody has seeded.
    /// </summary>
    /// <remarks>
    /// The demo would otherwise have to seed every random SKU before an order could succeed. Tests that
    /// need the unavailable path set a product's stock to zero explicitly, which is deterministic in a way
    /// that relying on an empty catalogue would not be.
    /// </remarks>
    public int DefaultStockQuantity { get; init; } = 1000;
}

/// <summary>
/// What the Inventory service does, independent of whether a command or an event asked for it.
/// </summary>
/// <param name="dbContext">The service's context.</param>
/// <param name="outbox">Stages outbound messages in the caller's transaction.</param>
/// <param name="faults">Fault dial.</param>
/// <param name="options">Inventory settings.</param>
/// <param name="timeProvider">Clock.</param>
public sealed class InventoryProcessor(
    InventoryDbContext dbContext,
    IOutboxWriter outbox,
    FaultInjector faults,
    IOptions<InventoryOptions> options,
    TimeProvider timeProvider)
{
    private readonly InventoryDbContext _dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly IOutboxWriter _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));

    private readonly FaultInjector _faults = faults ?? throw new ArgumentNullException(nameof(faults));

    private readonly InventoryOptions _options =
        options?.Value ?? throw new ArgumentNullException(nameof(options));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>Holds stock, or reports what could not be met. Stages the result; the caller commits.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="lines">What to hold.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ReserveAsync(
        Guid orderId,
        IReadOnlyList<OrderLine> lines,
        SagaVariant variant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_faults.ShouldDecline(nameof(ReserveAsync)))
        {
            _outbox.Stage(new InventoryUnavailable(
                orderId,
                variant,
                [.. lines.Select(static line => line.Sku)]));

            return;
        }

        // Quantities are totalled per product first. An order with two lines for the same product has to
        // be checked against the sum, not twice against the same number, or it can be accepted when only
        // half of it can actually be met.
        Dictionary<Guid, int> requested = [];
        foreach (OrderLine line in lines)
        {
            requested[line.Sku] = requested.GetValueOrDefault(line.Sku) + line.Quantity;
        }

        Guid[] skus = [.. requested.Keys];

        Dictionary<Guid, StockItem> stock = await _dbContext.Stock
            .Where(item => skus.Contains(item.Sku))
            .ToDictionaryAsync(item => item.Sku, cancellationToken);

        foreach (Guid sku in skus)
        {
            if (!stock.ContainsKey(sku))
            {
                StockItem created = StockItem.Create(sku, _options.DefaultStockQuantity);
                _dbContext.Stock.Add(created);
                stock[sku] = created;
            }
        }

        // Checked in full before anything is taken. Taking as we go and then rolling back would mean
        // clearing the change tracker, which would also discard the idempotency ledger entry staged for
        // this message and quietly reopen the double-reservation hole.
        List<Guid> unavailable =
        [
            .. requested
                .Where(entry => stock[entry.Key].Available < entry.Value)
                .Select(static entry => entry.Key)
        ];

        if (unavailable.Count > 0)
        {
            _outbox.Stage(new InventoryUnavailable(orderId, variant, unavailable));
            return;
        }

        foreach ((Guid sku, int quantity) in requested)
        {
            stock[sku].TryTake(quantity);
        }

        Reservation reservation = Reservation.Hold(
            orderId,
            lines.Select(static line => (line.Sku, line.Quantity)),
            now);

        _dbContext.Reservations.Add(reservation);
        _outbox.Stage(new InventoryReserved(orderId, variant, reservation.Id));
    }

    /// <summary>Puts held stock back. Stages the result; the caller commits.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="variant">Coordination strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ReleaseAsync(Guid orderId, SagaVariant variant, CancellationToken cancellationToken)
    {
        Reservation? reservation = await _dbContext.Reservations
            .Include(entity => entity.Lines)
            .FirstOrDefaultAsync(entity => entity.OrderId == orderId, cancellationToken);

        if (reservation is null)
        {
            // Nothing was ever held. That happens when inventory itself was the step that failed, and the
            // honest answer is to confirm the end state rather than fail and be redelivered forever.
            return;
        }

        if (reservation.Release(_timeProvider.GetUtcNow()))
        {
            Guid[] skus = [.. reservation.Lines.Select(static line => line.Sku)];

            Dictionary<Guid, StockItem> stock = await _dbContext.Stock
                .Where(item => skus.Contains(item.Sku))
                .ToDictionaryAsync(item => item.Sku, cancellationToken);

            foreach (ReservationLine line in reservation.Lines)
            {
                if (stock.TryGetValue(line.Sku, out StockItem? item))
                {
                    item.Restore(line.Quantity);
                }
            }
        }

        _outbox.Stage(new InventoryReleased(orderId, variant, reservation.Id));
    }

    /// <summary>Stages what an order contains, so a later event does not have to carry it.</summary>
    /// <param name="orderId">Order.</param>
    /// <param name="lines">Order lines.</param>
    public void RememberOrder(Guid orderId, IReadOnlyList<OrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        _dbContext.KnownOrders.Add(KnownOrder.Create(
            orderId,
            JsonSerializer.Serialize(lines, MessageTypeRegistry.SerializerOptions),
            _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Reads back what an order contains.
    /// </summary>
    /// <remarks>
    /// Throws when the order is not known yet. That is the correct response to the race between
    /// OrderCreated and PaymentAuthorized: the transport redelivers, and by then the earlier message has
    /// been processed. Guessing, or reserving nothing, would silently ship an empty order.
    /// </remarks>
    /// <param name="orderId">Order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<OrderLine>> RecalledLinesAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        KnownOrder? known = await _dbContext.KnownOrders
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.OrderId == orderId, cancellationToken);

        if (known is null)
        {
            throw new InvalidOperationException(
                $"Order {orderId} has not been recorded here yet. Retrying after OrderCreated lands.");
        }

        return JsonSerializer.Deserialize<List<OrderLine>>(
            known.LinesJson,
            MessageTypeRegistry.SerializerOptions) ?? [];
    }

    /// <summary>Sets a product's stock. Used by the demo seeder and by tests.</summary>
    /// <param name="sku">Product.</param>
    /// <param name="quantity">Units on hand.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetStockAsync(Guid sku, int quantity, CancellationToken cancellationToken)
    {
        StockItem? item = await _dbContext.Stock
            .FirstOrDefaultAsync(entity => entity.Sku == sku, cancellationToken);

        if (item is null)
        {
            _dbContext.Stock.Add(StockItem.Create(sku, quantity));
        }
        else
        {
            item.SetAvailable(quantity);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
