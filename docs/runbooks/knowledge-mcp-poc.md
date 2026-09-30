# Knowledge MCP proof of concept

## Status and scope

Knowledge sources on current `main` were read at revision `cfb4afb0025517260c896f9a737541eec91b1f52`.

The repository contains a separate remote MCP service at `tools/KnowledgeMcp`; it is not part of the Hotel API or web app. The service exposes only `search_knowledge`, uses the fixed `knowledge` index, and returns up to five canonical knowledge passages (up to 3,000 characters each) with their full Git revision, repository path/link, owner, last-reviewed date, and content hash. It requires a full 40-character revision, limits queries to 256 characters, rejects any wildcard or operator-only query with `invalid_query`, and uses Azure Search `SearchMode.All` plus exact whole-term validation so every submitted term must match the title or content. Provide focused subject keywords, not an arbitrary filler-rich natural-language question; the deployment smoke uses `managed identity Azure Search`, whose terms co-occur in the canonical security knowledge at revision `cfb4afb0025517260c896f9a737541eec91b1f52`. Long source documents are excerpted around complete matching terms: one bounded window is used when all content matches fit, otherwise separated snippets include each complete term. If the required terms cannot be represented within the 3,000-character bound, the service returns `incomplete_evidence` rather than claiming unsupported evidence. It returns explicit no-evidence results rather than substituting another revision or local content.

**This change is source, unit-test, and proposed workflow evidence only. It has not been deployed, assigned Search RBAC, configured in a Copilot client, or used for a live retrieval demonstration.** No Search index revision is assumed to exist. The deployment workflow must run from `main` with `reviewed_source_sha` equal to that exact main run's SHA and a coordinator-verified indexed `knowledge_revision`; do not merge or deploy before reviewed-source gates.

## Hosted deployment gate

`.github/workflows/deploy-knowledge-mcp.yml` is manual-only, requires the `main` ref and exact reviewed main SHA, and uses the existing GitHub Actions `development` environment OIDC secrets. `infra/knowledge-mcp.bicep` creates only a separate `ahb-dev-knowledge-mcp` app and `ahb-dev-knowledge-mcp-f1-plan` in West Europe, plus Search Index Data Reader scoped to the existing `ahb-dev-bj5rmi3w3ntgq-search` service. It does not deploy or mutate the Hotel API, SQL, or Search service.

The manual workflow requires exactly `reviewed_source_sha` (the full current main SHA) and `knowledge_revision` (the full, verified indexed revision). Provision the existing development environment secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, plus the new `KNOWLEDGE_MCP_ACCESS_TOKEN` described below. A successful run exposes the public MCP endpoint `https://ahb-dev-knowledge-mcp.azurewebsites.net/mcp` in the workflow step summary and the `deploy` job output named `public_endpoint`.

The existing paid Hotel B1 plan must not be reused: Azure inspection reported it is already highly utilized. A separate F1 Free Linux plan was verified available in West Europe. It may sleep and cold-start and is subject to Free-tier CPU quotas; no paid SKU fallback is permitted. If the Free plan cannot be provisioned within quota, stop and report the blocker without upgrading.

Before the coordinator dispatches after review gates, provision the **development GitHub Environment secret** `KNOWLEDGE_MCP_ACCESS_TOKEN` (32–128 URL-safe ASCII characters). The workflow passes it as a masked environment value into a mode-600 temporary secure Bicep parameter file; Bicep declares it `@secure()` and writes the app setting. The file is removed and its absence checked on every path. The Search endpoint is fixed by Bicep, and the managed identity receives only the Search Index Data Reader role scoped to that service. Azure credentials are acquired only by GitHub OIDC; no local Azure credential chain is used.

The service uses `ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)`, not a developer credential chain. Its managed identity must receive only Search Index Data Reader at the existing Search service scope. Its endpoint must be HTTPS and the MCP route is `/mcp`; all service routes, including `/health`, require the configured bearer token.

## Chat retrieval demonstration (first)

1. The coordinator provides `knowledge_revision`, a trusted full commit SHA verified against the existing Search index. The workflow’s live MCP call also requires actual evidence at that SHA. An unmerged revision or a successful local test is not proof of index presence.
2. Verify that the Copilot App chat client supports remote MCP and configure its own MCP client settings for the hosted HTTPS `/mcp` endpoint. This configuration is separate from GitHub's cloud-agent MCP settings; verify it in the actual App rather than assuming repository settings apply.
3. Configure the `Authorization` header with a bearer token through the App's protected secret mechanism. Never include the token in prompts, transcripts, or evidence.
4. Ask the App a question answered by a canonical knowledge document at the chosen SHA. Capture the actual MCP `tools/call` invocation and response, the cited repository path/link, and exact revision. Persist a sanitized result to a durable, revision/run-named workflow artifact.
5. Before reporting success, use the hosted endpoint to capture all negative cases: no Authorization header returns HTTP 401; omitted/empty revision returns `revision_required` (the tool input schema requires the field, so exercise an empty value for the tool-result case); a valid SHA with no matching passage returns `no_evidence`. Record the requested revision and response without recording the bearer token.

## Repository cloud-agent MCP registration and demonstration (after chat)

The user authorizes preparing this repository registration now. A repository administrator can configure the remote server under **Settings → Copilot → MCP servers**. This setting is shared by Copilot cloud agent and code review, and intentionally is not represented in `.github/copilot-instructions.md`.

Add the same value used by the service to the Copilot Agents secret `COPILOT_MCP_KNOWLEDGE_TOKEN`, then use this secret-backed configuration (replace only the public endpoint; never replace the secret placeholder with its value):

```json
{
  "mcpServers": {
    "azure-knowledge": {
      "type": "http",
      "url": "https://ahb-dev-knowledge-mcp.azurewebsites.net/mcp",
      "headers": {
        "Authorization": "Bearer $COPILOT_MCP_KNOWLEDGE_TOKEN"
      },
      "tools": ["search_knowledge"]
    }
  }
}
```

This configuration enables one tool only. Do not use `*`, OAuth, resources, or prompts. Ask the cloud development agent to invoke `search_knowledge` with a trusted indexed SHA and focused subject keywords grounded in canonical knowledge. Capture the actual tool call and cited response in a durable workflow artifact, then repeat the unauthenticated, omitted-revision, and no-hit checks. Do not claim the cloud-agent milestone from configuration alone.

The real Copilot App chat client has separate MCP configuration; GitHub repository MCP registration does not establish that App configuration. The chat retrieval demonstration remains the first client proof milestone. The deployment workflow protocol smoke proves the deployed endpoint and live Azure Search behavior, not either Copilot client demo.

## Implementation and test evidence

The implementation and bounded unit tests are in:

- `tools/KnowledgeMcp/Program.cs`
- `tools/KnowledgeMcp/KnowledgeMcpTools.cs`
- `tests/UnitTests/KnowledgeMcpTests.cs`
- `scripts/Test-KnowledgeMcpEndpoint.py`
- `.github/workflows/deploy-knowledge-mcp.yml`
- `infra/knowledge-mcp.bicep`

Build and run the focused tests only in the hosted cloud runner:

```sh
dotnet restore tools/KnowledgeMcp/KnowledgeMcp.csproj --locked-mode
dotnet test tests/UnitTests/AgenticHotelBooking.UnitTests.csproj --no-restore --filter FullyQualifiedName~KnowledgeMcpTests
```

These tests exercise validation, wildcard/operator rejection before repository access, exact revision filtering, result bounds, query-centered excerpts, citation metadata, canonical paths, no-evidence behavior, token checking, deployment-scope contracts, and the read-only tool annotation. They do not use live Azure Search or prove either Copilot client integration. No live endpoint, Azure RBAC, deployment, or workflow artifact is claimed by this runbook.

A hosted-runner loopback smoke additionally confirmed: an unauthenticated POST to `/mcp` returned HTTP 401; authenticated `tools/list` returned only `search_knowledge` with `readOnlyHint: true`; and an authenticated `tools/call` with an empty revision returned `revision_required` with no evidence. It used a dummy token and deliberately invalid Search endpoint, so it did not query Azure, establish a cited revision/no-hit against the live index, or demonstrate either Copilot client.

An in-session hosted-runner test also executed `scripts/Test-KnowledgeMcpEndpoint.py` against an isolated local HTTPS protocol fixture. It exercised initialize, tools/list, the cited-passage response shape, unauthorized rejection, empty revision, and nonexistent-revision handling, and verified the fixture token was absent from the temporary output. That fixture output was not retained as a deliverable; the fixture mocked Azure Search and is not live Search evidence.
