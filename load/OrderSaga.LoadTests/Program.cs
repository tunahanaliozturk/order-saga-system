using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using OrderSaga.Contracts;

// Drives a running stack and reports how long orders take, for each coordination strategy.
//
// Two numbers matter and they are not the same one. Acceptance latency is how long POST /orders took,
// which is a local database write and says nothing about the saga. Saga completion is measured afterwards
// by asking the order API when each order reached a terminal state, and that is the number a customer
// actually experiences.
//
// Written by hand rather than with a load-testing library. The two libraries worth using here are either
// commercially licensed or a separate binary with its own scripting language, and what this needs is an
// open-model request generator and a percentile: about eighty lines. See docs/adr/0006.
//
// Usage:
//   dotnet run --project load/OrderSaga.LoadTests -- [baseUrl] [ordersPerSecond] [seconds]

string baseUrl = Arg(0) ?? "http://localhost:5000";
int ordersPerSecond = int.Parse(Arg(1) ?? "150", CultureInfo.InvariantCulture);
int seconds = int.Parse(Arg(2) ?? "60", CultureInfo.InvariantCulture);

var serializer = new JsonSerializerOptions(JsonSerializerDefaults.Web);

using var handler = new SocketsHttpHandler
{
    // A load generator that queues behind its own connection pool measures the pool, not the system.
    MaxConnectionsPerServer = Math.Max(ordersPerSecond * 2, 64),
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
};

using var client = new HttpClient(handler)
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30),
};

Console.WriteLine($"Target {baseUrl}, {ordersPerSecond} orders/sec per route, {seconds}s.");
Console.WriteLine("Warming up.");
await WarmUpAsync();

Route[] routes =
[
    new("orchestrated", SagaVariant.Orchestrated, "/orders"),
    new("choreographed", SagaVariant.Choreographed, "/orders/choreographed"),
];

var placed = new ConcurrentDictionary<Guid, SagaVariant>();
var accepted = new ConcurrentDictionary<SagaVariant, ConcurrentBag<double>>();
var rejected = new ConcurrentDictionary<SagaVariant, int>();

foreach (Route route in routes)
{
    accepted[route.Variant] = [];
    rejected[route.Variant] = 0;
}

Console.WriteLine("Running.");
var wall = Stopwatch.StartNew();

// Open model: requests go out on a fixed schedule whether or not earlier ones have come back. Waiting for
// each response before sending the next measures the system at whatever rate it happens to allow, which
// is the load test telling you what you already knew.
List<Task> inFlight = [];

using (var tick = new PeriodicTimer(TimeSpan.FromSeconds(1)))
{
    for (int second = 0; second < seconds; second++)
    {
        foreach (Route route in routes)
        {
            for (int i = 0; i < ordersPerSecond; i++)
            {
                inFlight.Add(PlaceAsync(route));
            }
        }

        inFlight.RemoveAll(static task => task.IsCompleted);
        await tick.WaitForNextTickAsync();
    }
}

await Task.WhenAll(inFlight);
wall.Stop();

Console.WriteLine();
Console.WriteLine("Order acceptance, POST /orders only");
Console.WriteLine("route             sent   rejected      p50      p99");

foreach (Route route in routes)
{
    List<double> samples = [.. accepted[route.Variant]];
    if (samples.Count == 0)
    {
        continue;
    }

    samples.Sort();

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{route.Name,-14} {samples.Count,7} {rejected[route.Variant],10}  {Percentile(samples, 0.50),6:0} ms {Percentile(samples, 0.99),6:0} ms"));
}

Console.WriteLine();
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Placed {placed.Count} orders in {wall.Elapsed.TotalSeconds:0.0}s. Letting the sagas settle."));

await Task.Delay(TimeSpan.FromSeconds(30));

var completion = new Dictionary<SagaVariant, List<double>>();
var unfinished = new Dictionary<SagaVariant, int>();

foreach (Route route in routes)
{
    completion[route.Variant] = [];
    unfinished[route.Variant] = 0;
}

foreach ((Guid orderId, SagaVariant variant) in placed)
{
    JsonElement order = await client.GetFromJsonAsync<JsonElement>($"/orders/{orderId}", serializer);
    string status = order.GetProperty("status").GetString() ?? string.Empty;

    if (status is not ("Completed" or "Cancelled"))
    {
        unfinished[variant]++;
        continue;
    }

    DateTimeOffset createdAt = order.GetProperty("createdAt").GetDateTimeOffset();
    DateTimeOffset completedAt = order.GetProperty("completedAt").GetDateTimeOffset();

    completion[variant].Add((completedAt - createdAt).TotalMilliseconds);
}

Console.WriteLine();
Console.WriteLine("Saga completion, from order creation to a terminal state");
Console.WriteLine("route            orders  unfinished      p50      p99");

foreach (Route route in routes)
{
    List<double> samples = completion[route.Variant];
    if (samples.Count == 0)
    {
        continue;
    }

    samples.Sort();

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{route.Name,-14} {samples.Count,7} {unfinished[route.Variant],11}  {Percentile(samples, 0.50),6:0} ms {Percentile(samples, 0.99),6:0} ms"));
}

int stillRunning = unfinished.Values.Sum();
Console.WriteLine();
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Not terminal after 30s: {stillRunning} of {placed.Count} ({(placed.IsEmpty ? 0 : 100d * stillRunning / placed.Count):0.00}%)"));

return stillRunning == 0 ? 0 : 1;

async Task PlaceAsync(Route route)
{
    var request = new
    {
        customerId = Guid.CreateVersion7(),
        lines = new[] { new OrderLine(Guid.CreateVersion7(), 1, 19.99m) },
    };

    long started = Stopwatch.GetTimestamp();

    try
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(route.Path, request, serializer);

        if (!response.IsSuccessStatusCode)
        {
            rejected.AddOrUpdate(route.Variant, 1, static (_, count) => count + 1);
            return;
        }

        accepted[route.Variant].Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(serializer);
        placed[body.GetProperty("id").GetGuid()] = route.Variant;
    }
#pragma warning disable CA1031 // A load generator counts failures, it does not stop on them.
    catch (Exception)
#pragma warning restore CA1031
    {
        rejected.AddOrUpdate(route.Variant, 1, static (_, count) => count + 1);
    }
}

async Task WarmUpAsync()
{
    // The first request pays for connection setup, JIT and EF model building. Including it would put a
    // second-long outlier in the p99 and say nothing about the system under load.
    for (int attempt = 0; attempt < 30; attempt++)
    {
        try
        {
            HttpResponseMessage response = await client.GetAsync("/health/ready");
            if (response.IsSuccessStatusCode)
            {
                return;
            }
        }
#pragma warning disable CA1031 // The service may simply not be up yet.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Keep waiting.
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    throw new InvalidOperationException($"{baseUrl} never became ready.");
}

string? Arg(int index) => args.Length > index ? args[index] : null;

static double Percentile(List<double> sorted, double percentile) =>
    sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];

internal sealed record Route(string Name, SagaVariant Variant, string Path);
