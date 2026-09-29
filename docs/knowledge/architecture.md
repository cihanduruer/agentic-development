---
owner: Architecture owner
last_reviewed: 2026-09-29
---
# Architecture

## Application

The MVP is a modular monolith:

- Blazor WebAssembly serves the booking journey and operations dashboard.
- ASP.NET Core exposes hotel, availability, reservation, and agent-event APIs.
- Domain contains rules and records.
- Application contains use-case and telemetry contracts.
- Infrastructure contains replaceable implementations.

Entity Framework Core stores the catalog, reservations, and AI operations events in Azure SQL. The running API authenticates explicitly with its App Service system-assigned managed identity through Microsoft Entra ID and receives only the custom `hotel_booking_runtime` database role. The GitHub OIDC deployment principal, configured as the SQL Entra administrator, applies migrations and idempotently creates the contained API user; the runtime does not migrate schemas or use SQL administrator credentials. Reservation creation uses a serializable transaction and an indexed overlap query to preserve atomic overlap protection. Operations-event writes prune records older than the configured age and records beyond the configured capacity; reads clamp caller-supplied limits before issuing an ordered database query. Local and integration execution uses the EF in-memory provider with the same application services.

Static Web Apps rewrites client-side routes to the Blazor `index.html`, so direct navigation to pages such as `/operations` loads the SPA. Deployment writes the environment-specific HTTPS API endpoint to `appsettings.json` and removes publish-time Brotli and gzip variants of that runtime configuration; a browser must never receive a precompressed variant containing the local development endpoint.

## Operations

The API exposes a typed event-ingestion endpoint and broadcasts accepted events through SignalR after persistence succeeds. The dashboard reloads bounded recent history and receives live events. Dashboard reads and the SignalR hub remain public, while event ingestion and routing require the `Operations.Ingest` Entra application role outside Development. Production telemetry is correlated through Application Insights and retained in Log Analytics; sensitive prompt bodies and source code are not dashboard fields.

## Agent workflow

GitHub Copilot agents perform repository work. Deterministic C# policy owns safety, evidence, and approval gates. For ambiguous safe routes, Microsoft Agent Framework obtains strict typed output from an Azure OpenAI deployment and validates the suggestion against the live worker menu and confidence threshold. Azure AI Search supplies revisioned knowledge, Azure AI Content Safety supplies Prompt Shields, and the dedicated Microsoft Foundry evaluation workflow is configured to score groundedness when manually dispatched. These signals never grant authorization. Repository tests prove the deterministic fail-closed behavior; deployment and live-cloud workflow evidence are separate release artifacts and must not be inferred from local validation.

`tools/KnowledgeIndexer` reads only canonical Markdown under `docs/knowledge`, requires `owner` and `last_reviewed` front matter, chunks on section boundaries, and uses deterministic revision/path/content hashes as Azure AI Search keys. Re-running the same commit uses `mergeOrUpload` and does not duplicate chunks; prior revisions remain queryable.

Before any automated worker route is selected, the API invokes Azure AI Content Safety Prompt Shields over routing metadata and the live worker descriptions, then queries Azure AI Search for evidence matching the exact requested commit revision. Detection, missing or malformed evaluation evidence, configuration errors, and service errors all deterministically select `human_review`. Microsoft Agent Framework is reached only after these gates pass. The API authenticates to AI Services and Search with its Entra managed identity; local keys remain disabled. Model input is restricted to routing metadata and worker IDs. All decisions are emitted through the existing operations event stream and correlated in Application Insights.
