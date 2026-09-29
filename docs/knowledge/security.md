# Security

- GitHub Actions authenticates to Azure through OpenID Connect.
- Workloads use managed identity and Key Vault references.
- Production uses Microsoft Entra ID and least-privilege RBAC.
- Prompt and document inputs are untrusted and pass through Prompt Shields where applicable.
- Groundedness and task-adherence failures block high-impact actions.
- Operations events contain metadata only. Do not record prompts, source code, credentials, tokens, guest personal data, or tool output bodies.
- Correlation IDs are random identifiers, not user identifiers.
- CORS is allow-listed. Outside Development, operations ingestion and orchestration routing require an Entra v2 access token whose `aud` matches the configured API application client ID and whose `roles` claim contains `Operations.Ingest`. Token acquisition uses the API Application ID URI as a separate resource value. Missing authentication configuration prevents API startup.
- Development explicitly leaves operations writes and routing unauthenticated for local and integration-test use. Dashboard reads, SignalR, and public hotel-booking endpoints are not covered by the operations-writer policy.
- Event history is bounded by age, record count, and query limit. The deployed defaults are 30 days, 2,000 records, and 500 records per query.
- Dependencies, infrastructure, and application code are scanned in CI.
