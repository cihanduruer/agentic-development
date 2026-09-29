# Agentic Hotel Booking

A Microsoft-first sample for evidence-grounded agentic software delivery. The repository combines a hotel-booking application with a real-time operations dashboard for Jev routes, worker activity, evidence gates, and human approvals.

## Components

- **Blazor WebAssembly:** guest booking journey and agent operations dashboard.
- **ASP.NET Core API:** catalog, availability, reservations, typed agent-event ingestion, SignalR, and Jev routing.
- **Jev adapter:** TypeSafe System One `choice` request with live worker menus, confidence gating, shadow mode, and safe human fallback.
- **Knowledge center:** versioned product, domain, architecture, delivery, and security context under `docs/knowledge`.
- **Azure:** Bicep for Static Web Apps, App Service, Application Insights, Log Analytics, Key Vault, and Azure AI Search.
- **Delivery:** Azure Boards for work visibility and GitHub Actions for validation and deployment.

## Fully agentic intake

Azure Boards is the product intake system. Every new User Story or Bug in `sample-project` is discovered by the `Agentic intake` workflow within five minutes:

1. The workflow authenticates to Azure and Azure DevOps through GitHub OIDC.
2. It creates an idempotently linked GitHub issue containing the Azure Boards acceptance criteria.
3. It assigns `copilot-swe-agent` with the `hotel-developer` profile.
4. Copilot creates a branch, implements and tests the requirement, and opens a draft pull request containing `AB#<id>`.
5. Azure Boards moves to Active and receives the GitHub issue link.

The `github-synced` tag prevents duplicate dispatch. Product owners only create hotel User Stories or Bugs; platform work is intentionally not tracked in Azure Boards.

## Run locally

In separate terminals:

```powershell
dotnet run --project src\Api\AgenticHotelBooking.Api.csproj
dotnet run --project src\Web\AgenticHotelBooking.Web.csproj
```

Open:

- Booking application: <http://localhost:5051>
- Agent operations: <http://localhost:5051/operations>
- API health: <http://localhost:5224/health>

Record a development event:

```powershell
$body = @{
  kind = 1
  correlationId = "flow-001"
  workItemId = "AB#958"
  agent = "jev"
  summary = "Selected QA agent from the live worker menu"
  decision = "qa-agent"
  outcome = "shadow"
  confidence = 0.96
  durationMilliseconds = 31
  knowledgeRevision = (git rev-parse HEAD)
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
  -Uri http://localhost:5224/api/operations/events `
  -ContentType application/json `
  -Body $body
```

## Jev activation

Jev is deliberately disabled and in shadow mode by default. Set `TYPESAFE_API_KEY` through a secure environment or Key Vault reference, then override:

```json
{
  "Jev": {
    "Enabled": true,
    "Shadow": true,
    "MinimumConfidence": 0.8
  }
}
```

Run in shadow mode until routing accuracy, false-completion rate, latency, and cost meet an approved threshold. The adapter transmits capability labels and evidence metadata only. It never sends repository source, prompts, secrets, personal data, or document bodies.

## Validate

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore
dotnet test --no-build
az bicep build --file infra\main.bicep
```

## Azure

`agentic-hotelbookingdev` is the development resource group. Preview infrastructure changes before deployment:

```powershell
az deployment sub what-if `
  --location westeurope `
  --template-file infra\main.bicep `
  --parameters resourceGroupName=agentic-hotelbookingdev environment=dev
```

Production uses a separate resource group and a GitHub Environment with required human reviewers.
