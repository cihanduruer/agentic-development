---
owner: Security owner
last_reviewed: 2026-09-29
---
# Security

- GitHub Actions authenticates to Azure through OpenID Connect.
- The API uses its App Service managed identity for Azure SQL, Azure AI Services, and Azure AI Search. AI Services and Search local-key authentication is disabled.
- Azure SQL accepts Microsoft Entra authentication only. The runtime identity has a custom role limited to catalog reads, reservation reads/inserts, and operations-event reads/inserts/deletes for bounded retention; it is not a database owner and cannot migrate schemas.
- Prompt and worker-description inputs are untrusted and are sent to the Azure AI Content Safety Prompt Shields API before routing.
- Routing requires an Azure AI Search hit for the exact requested knowledge commit. Prompt attack detection, absent grounding, configuration failure, and service failure route to `human_review`.
- The dedicated manual Microsoft evaluation workflow is configured to invoke `azure.ai.evaluation.GroundednessEvaluator` and persist its JSON evidence. PR validation separately persists deterministic routing-gate test results. Until a successful workflow artifact exists for a revision, groundedness evaluation is configured but not live-verified for that revision.
- Operations events contain metadata only. Do not record prompts, source code, credentials, tokens, guest personal data, or tool output bodies.
- Correlation IDs are random identifiers, not user identifiers.
- CORS is allow-listed. Outside Development, operations ingestion and orchestration routing require an Entra v2 access token whose `aud` matches the configured API application client ID and whose `roles` claim contains `Operations.Ingest`. Token acquisition uses the API Application ID URI as a separate resource value. Missing authentication configuration prevents API startup.
- Development explicitly leaves operations writes and routing unauthenticated for local and integration-test use. Dashboard reads, SignalR, and public hotel-booking endpoints are not covered by the operations-writer policy.
- Event history is bounded by age, record count, and query limit. The deployed defaults are 30 days, 2,000 records, and 500 records per query.
- Dependencies, infrastructure, and application code are scanned in CI.

## SQL bootstrap

The SQL server's Microsoft Entra administrator must be the GitHub OIDC deployment service principal before the first pipeline run. This control-plane assignment is the unavoidable one-time manual prerequisite when the deploying identity cannot assign itself as SQL administrator. For the current development subscription it is already configured.

Each development deployment then performs the repeatable data-plane sequence:

1. Apply EF migrations as the Entra deployment principal.
2. Before any infrastructure mutation, classify the dedicated resource group's SQL state. A resource group with no SQL server is an initial deployment. An existing environment must contain exactly one server and must already name the GitHub deployment principal as SQL Entra administrator; missing, ambiguous, or inaccessible state fails closed before deployment. Existing SQL is excluded from the first Bicep phase so its current authentication mode remains live if migration or bootstrap fails.
3. Provision shared infrastructure without applying API configuration. If the API is new, create its system identity through `infra/api-identity.bicep`; an upgraded API is not modified during this phase. Initial-empty SQL is created Entra-only, while existing SQL is not changed.
4. Acquire an Azure SQL access token and run the repository-built `tools/SqlManagedIdentityBootstrapper` with the App Service name and principal object ID. The tool uses `Microsoft.Data.SqlClient` token authentication and parameterized inputs, so deployment does not depend on runner-provided SQL tooling or SQLCMD variable preprocessing.
5. Fail closed if the runtime role has permissions beyond the documented object grants, or if the API principal has direct grants, owns securables, or belongs to another database role; then grant membership only in `hotel_booking_runtime`.
6. Apply the managed-identity API setting while existing SQL authentication remains available, deploy the API, and prove a SQL-backed request with that identity. A failure stops before SQL Entra-only and leaves the legacy SQL authentication path available for operator rollback. Only after readiness succeeds may a separate SQL-only Bicep phase enforce Entra-only authentication; verify another SQL-backed request afterward.
7. Automatic development deployments never delete the legacy credential. A separately confirmed manual development run may remove only the active legacy `sql-connection-string` after migration, managed-identity bootstrap, API deployment, and the operations persistence smoke all succeed. Production requires its own exact cleanup confirmation and waits for both API health and a successful SQL-backed catalog request. Cleanup never creates or broadens a role assignment. It uses the supported Key Vault data-plane delete operation with output suppressed and metadata-only absence verification. If the deployment identity lacks pre-provisioned metadata/delete access constrained to that secret, the workflow fails closed and an approved operator must delete that exact active secret and rerun verification.
8. Remove the temporary GitHub runner firewall rule.

No SQL login or SQL administrator password is used by the running API or stored in GitHub or application settings. After the explicitly approved cleanup, upgraded environments verify the absence of the active legacy Key Vault secret. Key Vault soft-delete and purge protection can retain a recoverable deleted version for the configured retention period; the deployment does not purge it or delete the vault because permanent purge is a separately approved irreversible operation.
