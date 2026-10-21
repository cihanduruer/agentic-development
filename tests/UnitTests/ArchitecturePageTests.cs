namespace AgenticHotelBooking.UnitTests;

public sealed class ArchitecturePageTests
{
    [Fact]
    public void DeliveryViewsUseOrderedCardsWithoutCrossingLinesAndPillRequestWithHistory()
    {
        var operations = ReadSource("src/Web/Pages/Operations.razor");
        var operationStyles = ReadSource("src/Web/Pages/Operations.razor.css");
        var architecture = ReadSource("src/Web/Pages/Architecture.razor");
        var architectureStyles = ReadSource("src/Web/Pages/Architecture.razor.css");

        Assert.Contains("class=\"request-pill\"", operations);
        Assert.Contains("class=\"request-number\"", operations);
        Assert.Contains("<label for=\"delivery-request\">History</label>", operations);
        Assert.DoesNotContain("Choose a request", operations);
        Assert.Contains("border-radius: 999px", operationStyles);
        Assert.DoesNotContain(".delivery-step::before", operationStyles);
        Assert.DoesNotContain("map-links delivery-links", architecture);
        Assert.DoesNotContain(".delivery-node:nth-child(", architectureStyles);
        Assert.DoesNotContain(".delivery-node + .delivery-node::before", architectureStyles);
        var admin = ReadSource("src/Web/Pages/Admin.razor");
        Assert.Contains("<AdminNav />", admin);
        Assert.DoesNotContain("Manage sources in Knowledge Center", admin);
    }

    [Fact]
    public void SystemMapLabelsConnectionsAndExplainsProtocolsOnMobileToo()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");
        var styles = ReadSource("src/Web/Pages/Architecture.razor.css");

        Assert.Contains(">API</text>", page);
        Assert.Contains(">MCP</text>", page);
        Assert.Contains(">Data</text>", page);
        Assert.Contains("aria-label=\"Connection protocols\"", page);
        Assert.Contains("SQL/TDS", page);
        Assert.Contains("read-only MCP over HTTPS", page);
        Assert.Contains("HTTPS REST and authenticated SignalR", page);
        Assert.Contains(".connection-legend { grid-template-columns: 1fr; }", styles);
    }

    [Fact]
    public void ArchitecturePageShowsCompleteAgenticProcessAndHotelSystem()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");

        Assert.Contains("@page \"/admin/architecture\"", page);
        Assert.Contains("<PageTitle>Architecture</PageTitle>", page);
        Assert.Contains("<h1 id=\"architecture-title\">Architecture</h1>", page);
        foreach (var service in new[]
        {
            "Azure Static Web Apps", "Blazor WebAssembly", "App Service", "Azure SQL",
            "Azure AI Content Safety", "Azure AI Search", "Microsoft Agent Framework",
            "Blob Storage", "Communication Services Email", "Application Insights", "Log Analytics"
        })
        {
            Assert.Contains(service, page);
        }
        AssertOrdered(page,
            "\"Describe what you need\"", "\"Your request is picked up\"", "\"The AI builds your change\"",
            "\"Automatic checks run\"", "\"The AI reviews the code\"", "\"Check the agreed outcome\"",
            "\"You approve publishing\"", "\"Publish to the demo site\"", "\"Optional: release to production\"");
        Assert.Contains("aria-label=\"Requirement to deployment\"", page);
        Assert.Contains("aria-label=\"Optional supporting workflows\"", page);
        Assert.Contains("aria-label=\"Declared Azure services\"", page);
        Assert.Contains("hotel-developer", page);
        Assert.Contains("does not dispatch hotel-qa", page);
        Assert.Contains("not another YAML file", page);
        Assert.Contains("not the authoritative Hotel /mcp", page);
        Assert.Contains("not live runs or service health", page);
        Assert.Contains("production still needs explicit human approval", page);
        Assert.Contains("Microsoft-Decision-1", page);
        Assert.Contains("not implemented", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("service-paths", page);
    }

    [Fact]
    public void ArchitectureFlowUsesEveryActualWorkflowWithoutDuplicateCatalogue()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");
        Assert.DoesNotContain("workflows-title", page);
        Assert.DoesNotContain("workflow-catalogue", page);
        Assert.Contains("@workflow.Description", page);
        Assert.Contains("Optional tools, not delivery steps", page);
        var workflowDirectory = Path.GetDirectoryName(FindSource(".github/workflows/agentic-intake.yml"))
            ?? throw new DirectoryNotFoundException();
        var files = Directory.GetFiles(workflowDirectory, "*.yml");
        Assert.Equal(13, files.Length);
        foreach (var file in files)
        {
            var header = File.ReadLines(file).First();
            Assert.StartsWith("name: ", header);
            var name = header["name: ".Length..].Trim();
            Assert.Contains($"new(\"{name}\", \"{Path.GetFileName(file)}\",", page);
        }
    }

    [Fact]
    public void ArchitectureMotionIsDecorativeAndRespectsReducedMotion()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");
        var styles = ReadSource("src/Web/Pages/Architecture.razor.css");

        Assert.Contains("Read from top to bottom", page);
        Assert.Contains("aria-hidden=\"true\"", page);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles);
        Assert.Contains("animation: none;", styles);
        Assert.Contains("@media (max-width: 760px)", styles);
        Assert.Contains("@keyframes architecture-flow", styles);
        Assert.DoesNotContain("overflow-x: auto", styles);
        Assert.Contains("minmax(0, 1fr)", styles);
    }

    [Fact]
    public void DeliveryChartIsVerticalAndExplainsTypeOutcomeAndProductOwnerAction()
    {
        var page = ReadSource("src/Web/Pages/Architecture.razor");
        var styles = ReadSource("src/Web/Pages/Architecture.razor.css");

        Assert.Contains("grid-template-columns: minmax(0, 1fr)", styles);
        Assert.DoesNotContain(".delivery-grid { position: relative; display: grid; grid-template-columns: repeat(3", styles);
        Assert.Contains("@stage.Type", page);
        Assert.Contains("Human action", page);
        Assert.Contains("AI coding agent", page);
        Assert.Contains("AI reviewer + GitHub workflow", page);
        Assert.Contains("Human approval + API", page);
        Assert.Contains("<dt>What you get</dt>", page);
        Assert.Contains("<dt>Your part</dt>", page);
        Assert.Contains("<summary>Technical details</summary>", page);
        Assert.Contains("Automated checks may run together; production is a separate decision.", page);
        Assert.Contains("stage.Number is not (\"08\" or \"09\")", page);
        Assert.DoesNotContain("architecture-journey", page);
        Assert.DoesNotContain("architecture-metrics", page);
        Assert.Contains("<details class=\"architecture-technical\">", page);
        Assert.Contains("Technical reference: services, safety and optional tools", page);
        AssertOrdered(page, "id=\"system-map-title\"", "Who does each step, what you get, and when you need to act", "<details class=\"architecture-technical\">");
        Assert.Contains("href=\"/admin/architecture\">Architecture</NavLink>", ReadSource("src/Web/Layout/AdminNav.razor"));
        Assert.DoesNotContain("Hotel Architecture", ReadSource("src/Web/Pages/ArchitectureRedirect.razor"));
    }

    [Fact]
    public void AdminPageIsAProductManagementShellAndDeliveryStatusServiceKeepsOrderedStageEvidence()
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
        Assert.Contains("<PageTitle>AI Product Management</PageTitle>", page);
        Assert.Contains("<h1 id=\"product-management-title\">AI Product Management</h1>", page);
        Assert.DoesNotContain("api/delivery/status", page);
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
        Assert.Contains("delivery-status-badge.running { animation: none; }", ReadSource("src/Web/wwwroot/css/app.css"));
    }

    [Fact]
    public void PublicNavigationExposesOnlyHomeAndTheProductManagementEntry()
    {
        var navigation = ReadSource("src/Web/Layout/NavMenu.razor");
        var adminNav = ReadSource("src/Web/Layout/AdminNav.razor");
        var home = ReadSource("src/Web/Pages/Home.razor");
        var operations = ReadSource("src/Web/Pages/Operations.razor");
        var layout = ReadSource("src/Web/Layout/MainLayout.razor");
        var admin = ReadSource("src/Web/Pages/Admin.razor");
        var architecture = ReadSource("src/Web/Pages/Architecture.razor");

        Assert.Contains("href=\"\"", navigation);
        Assert.Contains("href=\"admin\"", navigation);
        Assert.Contains("AI Product Management", navigation);
        Assert.DoesNotContain("operations", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("architecture", navigation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin", home, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<PageTitle>Hotel Booking</PageTitle>", home);
        Assert.DoesNotContain("Agentic Hotel Booking", home, StringComparison.Ordinal);
        foreach (var publicDeliveryTerm in new[] { "agent operations", "architecture", "delivery", "demo", "sample hotel" })
        {
            Assert.DoesNotContain(publicDeliveryTerm, home, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("access gap", adminNav, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@page \"/admin/operations\"", operations);
        Assert.Contains("<main>", layout);
        Assert.Contains("<section class=\"product-management\" aria-labelledby=\"product-management-title\">", admin);
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

    private static string ReadSource(string relativePath) => File.ReadAllText(FindSource(relativePath));

    private static string FindSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relativePath}'.");
    }
}
