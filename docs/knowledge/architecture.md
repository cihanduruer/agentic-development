# Architecture

## Application

The MVP is a modular monolith:

- Blazor WebAssembly serves the booking journey and operations dashboard.
- ASP.NET Core exposes hotel, availability, reservation, and agent-event APIs.
- Domain contains rules and records.
- Application contains use-case and telemetry contracts.
- Infrastructure contains replaceable implementations.

Entity Framework Core stores the catalog and reservations in Azure SQL. Reservation creation uses a serializable transaction and an indexed overlap query to preserve atomic overlap protection. Local and integration execution uses the EF in-memory provider with the same application service.

## Operations

The API exposes a typed event-ingestion endpoint and broadcasts accepted events through SignalR. The dashboard reloads recent history and receives live events. Production telemetry is correlated through Application Insights and retained in Log Analytics; sensitive prompt bodies and source code are not dashboard fields.

## Agent workflow

GitHub Copilot agents perform repository work. Deterministic C# policy owns safety, evidence, and approval gates. For ambiguous safe routes, Microsoft Agent Framework obtains strict typed output from an Azure OpenAI deployment and validates the suggestion against the live worker menu and confidence threshold. Azure AI Search supplies revisioned knowledge. Microsoft Foundry evaluators and Azure AI Content Safety provide retrieval, groundedness, task-adherence, and prompt-attack signals; signals never grant authorization.

The API authenticates to Azure AI Services with its Entra managed identity. Model input is restricted to routing metadata and worker IDs. All decisions are emitted through the existing operations event stream and correlated in Application Insights.
