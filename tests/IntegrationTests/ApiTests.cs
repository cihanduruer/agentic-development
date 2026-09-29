using System.Net;
using System.Net.Http.Json;
using AgenticHotelBooking.Application;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthReturnsHealthy()
    {
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HotelsReturnsSeededCatalog()
    {
        var response = await client.GetAsync("/api/hotels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Canal House", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RecordedAgentEventAppearsInRecentHistory()
    {
        var request = new RecordAgentEventRequest(
            AgentEventKind.RouteDecided,
            "flow-42",
            "AB#958",
            "jev",
            "Selected the QA worker from the live worker menu.",
            "qa-agent",
            "shadow",
            0.94,
            27,
            "abc123");

        var created = await client.PostAsJsonAsync("/api/operations/events", request);
        var events = await client.GetFromJsonAsync<AgentEvent[]>("/api/operations/events");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(events!, item => item.CorrelationId == "flow-42" && item.Decision == "qa-agent");
    }

    [Fact]
    public async Task DisabledJevRoutesSafelyToHumanReview()
    {
        var request = new RoutingRequest(
            "route-42",
            "AB#958",
            "quality-assurance",
            "browser-testing",
            "reversible",
            true,
            new Dictionary<string, string>
            {
                ["qa-agent"] = "Runs browser and API tests.",
                ["human_review"] = "Handles uncertain or disallowed routes."
            },
            "abc123");

        var response = await client.PostAsJsonAsync("/api/orchestration/route", request);
        var decision = await response.Content.ReadFromJsonAsync<RoutingDecision>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("human_review", decision!.EffectiveWorker);
        Assert.True(decision.Shadow);
    }
}
