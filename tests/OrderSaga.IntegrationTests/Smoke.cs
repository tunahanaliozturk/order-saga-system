namespace OrderSaga.IntegrationTests;

/// <summary>Proves the harness can bring the whole system up before anything else is asserted.</summary>
[Collection(InfrastructureFixtureBinding.Name)]
public sealed class HarnessSmokeTests(ContainerFixture containers)
{
    [Fact]
    public async Task All_four_services_start_and_report_ready()
    {
        await using SagaSystem system = await SagaSystem.StartAsync(containers, "smoke");

        foreach (ServiceName service in Enum.GetValues<ServiceName>())
        {
            HttpResponseMessage response = await system.Client(service).GetAsync("/health/ready");
            Assert.True(response.IsSuccessStatusCode, $"{service} was not ready: {response.StatusCode}");
        }
    }
}
