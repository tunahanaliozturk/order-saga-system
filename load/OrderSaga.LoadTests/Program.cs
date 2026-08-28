using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using NBomber.Contracts;
using NBomber.CSharp;
using OrderSaga.Contracts;

// Drives a running stack and reports how long orders take end to end, for each coordination strategy.
//
// Two numbers matter and they are not the same one. NBomber measures how long the API took to accept an
// order, which is a local database write and says nothing about the saga. Saga completion is measured
// separately, by asking the order API when each order reached a terminal state, because that is the
// number a customer actually experiences.
//
// Usage:
//   dotnet run --project load/OrderSaga.LoadTests -- [baseUrl] [ordersPerSecond] [durationSeconds]

string baseUrl = args.Length > 0 ? args[0] : "http://localhost:5000";
int ordersPerSecond = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 150;
int durationSeconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 300;

var serializer = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var placed = new System.Collections.Concurrent.ConcurrentDictionary<Guid, SagaVariant>();

using var client = new HttpClient
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(30),
};

ScenarioProps Placement(string name, SagaVariant variant, string route) =>
    Scenario.Create(name, async _ =>
    {
        var request = new
        {
            customerId = Guid.CreateVersion7(),
            lines = new[] { new OrderLine(Guid.CreateVersion7(), 1, 19.99m) },
        };

        HttpResponseMessage response = await client.PostAsJsonAsync(route, request, serializer);

        if (!response.IsSuccessStatusCode)
        {
            return Response.Fail(statusCode: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(serializer);
        placed[body.GetProperty("id").GetGuid()] = variant;

        return Response.Ok();
    })
    .WithoutWarmUp()
    .WithLoadSimulations(
        Simulation.Inject(
            rate: ordersPerSecond,
            interval: TimeSpan.FromSeconds(1),
            during: TimeSpan.FromSeconds(durationSeconds)));

NBomberRunner
    .RegisterScenarios(
        Placement("orchestrated", SagaVariant.Orchestrated, "/orders"),
        Placement("choreographed", SagaVariant.Choreographed, "/orders/choreographed"))
    .WithReportFileName("order-saga-load")
    .Run();

Console.WriteLine($"Placed {placed.Count} orders. Waiting for the sagas to settle.");

await Task.Delay(TimeSpan.FromSeconds(30));

var completions = new Dictionary<SagaVariant, List<double>>
{
    [SagaVariant.Orchestrated] = [],
    [SagaVariant.Choreographed] = [],
};

int stuck = 0;

foreach ((Guid orderId, SagaVariant variant) in placed)
{
    JsonElement order = await client.GetFromJsonAsync<JsonElement>($"/orders/{orderId}", serializer);

    string status = order.GetProperty("status").GetString() ?? string.Empty;
    if (status is not ("Completed" or "Cancelled"))
    {
        stuck++;
        continue;
    }

    DateTimeOffset createdAt = order.GetProperty("createdAt").GetDateTimeOffset();
    DateTimeOffset completedAt = order.GetProperty("completedAt").GetDateTimeOffset();

    completions[variant].Add((completedAt - createdAt).TotalMilliseconds);
}

Console.WriteLine();
Console.WriteLine("Saga completion, measured from order creation to terminal state");
Console.WriteLine("variant          orders      p50        p99");

foreach ((SagaVariant variant, List<double> samples) in completions)
{
    if (samples.Count == 0)
    {
        continue;
    }

    samples.Sort();

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{variant,-16} {samples.Count,6}  {Percentile(samples, 0.50),7:0} ms  {Percentile(samples, 0.99),7:0} ms"));
}

Console.WriteLine();
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Not terminal after 30s: {stuck} of {placed.Count} ({(placed.IsEmpty ? 0 : 100d * stuck / placed.Count):0.00}%)"));

static double Percentile(List<double> sorted, double percentile) =>
    sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];
