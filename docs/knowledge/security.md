# Security

- GitHub Actions authenticates to Azure through OpenID Connect.
- Workloads use managed identity and Key Vault references.
- Production uses Microsoft Entra ID and least-privilege RBAC.
- Prompt and document inputs are untrusted and pass through Prompt Shields where applicable.
- Groundedness and task-adherence failures block high-impact actions.
- Operations events contain metadata only. Do not record prompts, source code, credentials, tokens, guest personal data, or tool output bodies.
- Correlation IDs are random identifiers, not user identifiers.
- CORS is allow-listed. Production ingestion requires authenticated workload identity.
- Dependencies, infrastructure, and application code are scanned in CI.
