using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgenticHotelBooking.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgenticHotelBooking.IntegrationTests;

public sealed class OperationsAuthorizationTests
{
    [Fact]
    public void DeployedEnvironmentFailsClosedWithoutEntraConfiguration()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Production"));

        Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task DeployedEnvironmentRejectsAnonymousIngestionAndRouting()
    {
        await using var factory = new SecuredApiFactory();
        using var client = factory.CreateClient();

        var ingestion = await client.PostAsJsonAsync("/api/operations/events", CreateEvent());
        var routing = await client.PostAsJsonAsync("/api/orchestration/route", CreateRoute());

        Assert.Equal(HttpStatusCode.Unauthorized, ingestion.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, routing.StatusCode);
    }

    [Fact]
    public async Task DeployedEnvironmentRequiresOperationsIngestRole()
    {
        await using var factory = new SecuredApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        client.DefaultRequestHeaders.Add("X-Test-Role", "Reader");

        var response = await client.PostAsJsonAsync("/api/operations/events", CreateEvent());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizedWorkloadCanIngestWhileDashboardReadRemainsPublic()
    {
        await using var factory = new SecuredApiFactory();
        using var writer = factory.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        writer.DefaultRequestHeaders.Add("X-Test-Role", "Operations.Ingest");

        var created = await writer.PostAsJsonAsync("/api/operations/events", CreateEvent());
        using var dashboard = factory.CreateClient();
        var events = await dashboard.GetFromJsonAsync<AgentEvent[]>("/api/operations/events");
        var hotels = await dashboard.GetAsync("/api/hotels");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(events!, item => item.CorrelationId == "secured-flow");
        Assert.Equal(HttpStatusCode.OK, hotels.StatusCode);
    }

    private static RecordAgentEventRequest CreateEvent() =>
        new(
            AgentEventKind.WorkReceived,
            "secured-flow",
            "AB#958",
            "intake",
            "Accepted secured work.",
            "",
            "",
            null,
            2,
            "2dc64f5");

    private static RoutingRequest CreateRoute() =>
        new(
            "secured-route",
            "AB#958",
            "quality-assurance",
            "browser-testing",
            "reversible",
            true,
            new Dictionary<string, string>
            {
                ["qa-agent"] = "Runs browser and API tests."
            },
            "2dc64f5");

    private sealed class SecuredApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting(
                "OperationsAuth:Authority",
                "https://login.microsoftonline.com/test/v2.0");
            builder.UseSetting("OperationsAuth:Audience", "api://operations");
            builder.UseSetting("OperationsAuth:RequiredRole", "Operations.Ingest");
            builder.ConfigureTestServices(services =>
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { }));
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Role", out var role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new(ClaimTypes.NameIdentifier, "test-workload"),
                new(ClaimTypes.Role, role.ToString())
            ];
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
