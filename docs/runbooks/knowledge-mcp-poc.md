# Knowledge MCP proof of concept

## Status and scope

Knowledge sources were read at repository revision `38554d26d079dbc4804ea62c61c5124c59d6a8ce`.

The repository contains a separate remote MCP service at `tools/KnowledgeMcp`; it is not part of the Hotel API or web app. The service exposes only `search_knowledge`, uses the fixed `knowledge` index, and returns up to five canonical knowledge passages (up to 3,000 characters each) with their full Git revision, repository path/link, owner, last-reviewed date, and content hash. It requires a full 40-character revision, limits queries to 256 characters, and returns explicit `revision_required`, `invalid_revision`, or `no_evidence` results rather than substituting another revision or local content.

**This change is source and unit-test evidence only. It has not been deployed, assigned Search RBAC, configured in a Copilot client, or used for a live retrieval demonstration.** No Search index revision is assumed to exist. Do not merge or deploy before coordinator authorization of the reviewed source.

## Hosted deployment gate

The source-only POC intentionally does not add deployment infrastructure or dispatch a workflow. Although `infra/modules/platform.bicep` defines a development B1 App Service plan and a Search service, no cloud inspection in this change established that sharing the plan is safe for the running Hotel API, or that the deployment identity has the narrowly required assignment permissions. Do not deploy by running Azure commands from a developer machine.

Before adding an OIDC deployment workflow or dispatching one, the coordinator must:

1. Verify in the `agentic-hotelbookingdev` resource group that the existing plan and Search service are the intended development resources and that the plan has safe capacity for a separate app without restarting or degrading the Hotel API.
2. If the existing plan is unsuitable, stop and provide the cost and approval required for isolated compute before widening scope.
3. Authorize a manual, development-only GitHub Actions deployment using the repository's OIDC identity. It may create only the separate MCP web app and a `Search Index Data Reader` role assignment scoped to the existing Search service. Do not change the Hotel API, web app, SQL, or Search local-auth setting.
4. Configure the MCP app with `KnowledgeMcp__SearchEndpoint=https://<existing-search-service>.search.windows.net/` and `KnowledgeMcp__AccessToken` in protected Azure app settings. The token must be 32–128 URL-safe ASCII characters. Keep the same rotatable token in the separately authorized client secret store; never print or put it in source, workflow artifacts, command arguments in logs, or PR comments.

The service uses `ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)`, not a developer credential chain. Its managed identity must receive only Search Index Data Reader at the existing Search service scope. Its endpoint must be HTTPS and the only MCP route is `/mcp`; all requests to that route without the configured bearer token are rejected.

## Chat retrieval demonstration (first)

1. Obtain a trusted full commit SHA that the coordinator has verified is already indexed. An unmerged revision or a successful local test is not proof of index presence.
2. Verify that the Copilot App chat client supports remote MCP and configure its own MCP client settings for the hosted HTTPS `/mcp` endpoint. This configuration is separate from GitHub's cloud-agent MCP settings; verify it in the actual App rather than assuming repository settings apply.
3. Configure the `Authorization` header with a bearer token through the App's protected secret mechanism. Never include the token in prompts, transcripts, or evidence.
4. Ask the App a question answered by a canonical knowledge document at the chosen SHA. Capture the actual MCP `tools/call` invocation and response, the cited repository path/link, and exact revision. Persist a sanitized result to a durable, revision/run-named workflow artifact.
5. Before reporting success, use the hosted endpoint to capture all negative cases: no Authorization header returns HTTP 401; omitted/empty revision returns `revision_required` (the tool input schema requires the field, so exercise an empty value for the tool-result case); a valid SHA with no matching passage returns `no_evidence`. Record the requested revision and response without recording the bearer token.

## Cloud development-agent demonstration (second)

Only after the chat demonstration succeeds, a repository administrator can configure the remote server under **Settings → Copilot → MCP servers**. This is a GitHub UI setting and is intentionally not represented in `.github/copilot-instructions.md` or a client config file in this PR.

Add the same value used by the service to the Copilot Agents secret `COPILOT_MCP_KNOWLEDGE_TOKEN`, then use this secret-backed configuration (replace only the public endpoint; never replace the secret placeholder with its value):

```json
{
  "mcpServers": {
    "azure-knowledge": {
      "type": "http",
      "url": "https://<approved-host>/mcp",
      "headers": {
        "Authorization": "Bearer $COPILOT_MCP_KNOWLEDGE_TOKEN"
      },
      "tools": ["search_knowledge"]
    }
  }
}
```

This configuration enables one tool only. Do not use `*`, OAuth, resources, or prompts. Ask the cloud development agent to invoke `search_knowledge` with a trusted indexed SHA and a question grounded in canonical knowledge. Capture the actual tool call and cited response in a durable workflow artifact, then repeat the unauthenticated, omitted-revision, and no-hit checks. Do not claim the cloud-agent milestone from configuration alone.

## Implementation and test evidence

The implementation and bounded unit tests are in:

- `tools/KnowledgeMcp/Program.cs`
- `tools/KnowledgeMcp/KnowledgeMcpTools.cs`
- `tests/UnitTests/KnowledgeMcpTests.cs`

Build and run the focused tests only in the hosted cloud runner:

```sh
dotnet restore tools/KnowledgeMcp/KnowledgeMcp.csproj --locked-mode
dotnet test tests/UnitTests/AgenticHotelBooking.UnitTests.csproj --no-restore --filter FullyQualifiedName~KnowledgeMcpTests
```

These tests exercise validation, exact revision filtering, result bounds, citation metadata, no-evidence behavior, token checking, and the read-only tool annotation. They do not use live Azure Search or prove either Copilot client integration. No live endpoint, Azure RBAC, deployment, or workflow artifact is claimed by this runbook.

A hosted-runner loopback smoke additionally confirmed: an unauthenticated POST to `/mcp` returned HTTP 401; authenticated `tools/list` returned only `search_knowledge` with `readOnlyHint: true`; and an authenticated `tools/call` with an empty revision returned `revision_required` with no evidence. It used a dummy token and deliberately invalid Search endpoint, so it did not query Azure, establish a cited revision/no-hit against the live index, or demonstrate either Copilot client.
