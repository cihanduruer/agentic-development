namespace AgenticHotelBooking.UnitTests;

public sealed class ArchitecturePageTests
{
    [Fact]
    public void ArchitecturePageShowsHotelServicesNumberedFlowsAndDeclaredResponsibilities()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");

        Assert.Contains("@page \"/admin/architecture\"", page);
        Assert.Contains("<h1 id=\"architecture-title\">Hotel services and delivery path</h1>", page);
        Assert.Contains("Static Web Apps", page);
        Assert.Contains("Blazor WebAssembly", page);
        Assert.Contains("App Service", page);
        Assert.Contains("SignalR operations hub", page);
        Assert.Contains("Azure SQL", page);
        Assert.Contains("Azure AI Content Safety", page);
        Assert.Contains("Azure AI Search", page);
        Assert.Contains("Azure OpenAI via Microsoft Agent Framework", page);
        Assert.Contains("Application Insights + Log Analytics", page);
        Assert.Contains("GitHub Actions intake", page);
        Assert.Contains("GitHub issue + cloud agent", page);
        Assert.Contains("Pull request + evidence gates", page);
        Assert.Contains("Live delivery-status read path", page);
        Assert.Contains("GET /api/delivery/status", page);
        Assert.Contains("Fixed public GETs", page);
        Assert.Contains("Selected AB# GET · human provisioned", page);
        Assert.Contains("Azure Boards REST v7.1", page);
        Assert.Contains("not proven by this diagram", page);
        Assert.Contains("service-connector", page);
        Assert.Contains("connector-label", page);
        Assert.Contains("1 · HTTPS/API", page);
        Assert.Contains("2 · Managed identity / EF Core", page);
        Assert.Contains("1 · Telemetry configuration", page);
        Assert.Contains("2 · Workspace-backed", page);
        Assert.Contains("1 · Scheduled query", page);
        Assert.Contains("2 · Linked issue + assignment", page);
        Assert.Contains("3 · Agent opens PR", page);
        Assert.Contains("4 · Evidence-passed merge", page);
        AssertOrdered(page,
            "<h4>Azure Boards</h4>",
            "<h4>GitHub Actions intake</h4>",
            "<h4>GitHub issue + cloud agent</h4>",
            "<h4>Pull request + evidence gates</h4>",
            "<h4>Development deployment</h4>");
        AssertOrdered(page,
            "<h4>App Service API</h4>",
            "<h4>Application Insights</h4>",
            "<h4>Log Analytics workspace</h4>");
        Assert.Contains("aria-label=\"Guest booking service path\"", page);
        Assert.Contains("aria-label=\"API telemetry configuration path\"", page);
        Assert.Contains("Numbered flow explanations", page);
        Assert.Contains("Declared tier and responsibility", page);
        Assert.Contains("End-to-end delivery flow", page);
        Assert.Contains("Microsoft-Decision-1", page);
        Assert.Contains("shadow mode only", page);
        Assert.Contains("not implemented", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchitectureMotionIsDecorativeAndRespectsReducedMotion()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");
        var styles = ReadSource("src/Web/Pages/Architecture.razor.css");

        Assert.Contains("Decorative moving dot", page);
        Assert.Contains("aria-hidden=\"true\"", page);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles);
        Assert.Contains("animation: none;", styles);
        Assert.Contains("@media (max-width: 1000px)", styles);
    }

    [Fact]
    public void AdminOverviewUsesLiveDeliveryApiAndAccessibleOrderedStageEvidence()
    {
        var page = ReadSource("src/Web/Pages/Admin.razor");
        var deliveryService = ReadSource("src/Infrastructure/DeliveryStatusService.cs");
        var badge = ReadSource("src/Web/Shared/DeliveryBadge.razor");
        var expectedStages = new[]
        {
            "Boards intake", "GitHub issue and cloud agent", "Pull request", "PR validation",
            "Copilot review gate", "QA evidence", "Merge", "Development deployment",
            "Lifecycle sync and delivery evidence", "Release proposal", "Human production approval"
        };

        Assert.Contains("@page \"/admin\"", page);
        Assert.Contains("api/delivery/status", page);
        Assert.Contains("Select a recent delivery", page);
        Assert.Contains("Recent deliveries", page);
        Assert.Contains("refreshed every 20 seconds", page);
        Assert.Contains("deliveryStatus.isVisible", page);
        Assert.Contains("End-to-end status flow", page);
        Assert.Contains("<ol class=\"delivery-flow\"", page);
        Assert.Contains("aria-describedby=\"delivery-flow-help\"", page);
        Assert.Contains("Open evidence", page);
        Assert.Contains("Not connected (reason:", deliveryService);
        Assert.Contains("BoardsStatus:Organization", deliveryService);
        Assert.Contains("GitHubStatus:Token", deliveryService);
        Assert.Contains("\"Skipped\"", badge);
        foreach (var stage in expectedStages)
        {
            Assert.Contains($"\"{stage}\"", deliveryService);
        }

        Assert.Contains("\"Passed\"", deliveryService);
        Assert.Contains("\"Running\"", deliveryService);
        Assert.Contains("\"Failed\"", deliveryService);
        Assert.Contains("\"Waiting\"", deliveryService);
        Assert.Contains("\"Not started\"", deliveryService);
        Assert.Contains("Last update", page);
        Assert.Contains("Duration:", page);
        Assert.Contains("GitHub issue #", page);
        Assert.Contains("Pull request #", page);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", ReadSource("src/Web/Pages/Admin.razor.css"));
        Assert.Contains("delivery-status-badge.running { animation: none; }", ReadSource("src/Web/wwwroot/css/app.css"));
    }

    [Fact]
    public void PublicNavigationIsBookingOnlyAndAdminAccessGapIsDisclosed()
    {
        var navigation = ReadSource("src/Web/Layout/NavMenu.razor");
        var adminNav = ReadSource("src/Web/Layout/AdminNav.razor");
        var home = ReadSource("src/Web/Pages/Home.razor");
        var operations = ReadSource("src/Web/Pages/Operations.razor");
        var layout = ReadSource("src/Web/Layout/MainLayout.razor");
        var admin = ReadSource("src/Web/Pages/Admin.razor");
        var architecture = ReadSource("src/Web/Pages/Architecture.razor");

        Assert.Contains("href=\"\"", navigation);
        Assert.DoesNotContain("operations", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("architecture", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", home, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<PageTitle>Hotel Booking</PageTitle>", home);
        Assert.DoesNotContain("Agentic Hotel Booking", home, StringComparison.Ordinal);
        foreach (var publicDeliveryTerm in new[] { "agent operations", "architecture", "delivery", "demo", "sample hotel" })
        {
            Assert.DoesNotContain(publicDeliveryTerm, home, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("no sign-in or authorization configured", adminNav);
        Assert.Contains("@page \"/admin/operations\"", operations);
        Assert.Contains("<main>", layout);
        Assert.Contains("<section class=\"delivery-overview\" aria-labelledby=\"delivery-overview-title\">", admin);
        Assert.Contains("<section class=\"architecture-page\" aria-labelledby=\"architecture-title\">", architecture);
        Assert.DoesNotContain("<main", admin, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<main", architecture, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("@page \"/architecture\"", ReadSource("src/Web/Pages/ArchitectureRedirect.razor"));
        Assert.Contains("NavigateTo(\"/admin/architecture\", replace: true)", ReadSource("src/Web/Pages/ArchitectureRedirect.razor"));
        Assert.Contains("@page \"/operations\"", ReadSource("src/Web/Pages/OperationsRedirect.razor"));
        Assert.Contains("NavigateTo(\"/admin/operations\", replace: true)", ReadSource("src/Web/Pages/OperationsRedirect.razor"));
        Assert.Contains("@page \"/not-found\"", ReadSource("src/Web/Pages/NotFound.razor"));
    }

    [Fact]
    public void BoardsConnectorProvisioningIsManualAndNoCredentialIsDeclaredInBicep()
    {
        var platform = ReadSource("infra/modules/platform.bicep");
        var runbook = ReadSource("docs/runbooks/boards-status-connector.md");

        Assert.Contains("BoardsStatus__Organization", platform);
        Assert.Contains("ai-enabled-ado-org", platform);
        Assert.Contains("BoardsStatus__Project", platform);
        Assert.Contains("sample-project", platform);
        Assert.Contains("BoardsStatus__AuthMode", platform);
        Assert.Contains("ManagedIdentity", platform);
        Assert.DoesNotContain("BoardsStatus__Pat", platform);
        Assert.DoesNotContain("GitHubStatus__Token", platform);
        Assert.Contains("Basic", runbook);
        Assert.Contains("Readers", runbook);
        Assert.Contains("unverified", runbook, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertOrdered(string text, params string[] values)
    {
        var positions = values.Select(value => text.IndexOf(value, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order().ToArray(), positions);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}'.");
    }
}
