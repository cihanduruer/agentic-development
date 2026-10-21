# Knowledge Center and Copilot MCP

The application knowledge authority is the hosted Knowledge Center. Implementation decisions are in [ADR 0004](../adr/0004-unified-knowledge-center-and-mcp.md). Git delivery and security policy remains in the repository.

## Endpoints and configuration

- Website: `/admin/knowledge` on the development Hotel site.
- MCP: `https://ahb-dev-bj5rmi3w3ntgq-api.azurewebsites.net/mcp`.
- Required API settings: `KnowledgeStorage__Endpoint`, existing `WorkItemChat__*` Search/model endpoints, `KnowledgeAccess__McpToken`, `KnowledgeAccess__ManagementToken`.
- Hosted management: open AI Product Management or any administrator deep link; `/admin/login` requests an approved email and code, then returns to that page. One login covers the assistant, Knowledge Center, architecture and operations. Home and guest booking remain public. Codes expire after five minutes, are single-use and allow at most five guesses. Resends are limited to once per minute; both sign-in endpoints also have a per-instance IP request limit.
- Email settings: `KnowledgeSignIn__AllowedEmails` (comma-separated administrator addresses), `KnowledgeSignIn__EmailEndpoint`, and `KnowledgeSignIn__SenderAddress`. Missing configuration fails closed. Provision a development Azure-managed sender with `infra/knowledge-email.bicep`; API managed identity uses a custom ACS-scoped read/write role required by Microsoft's email authentication sample. No SMTP password or ACS access key is used.
- Successful sign-in creates an eight-hour administrator session, stored in browser `sessionStorage` so refreshing the same tab does not ask for another code. The shared navigation shows the approved address and Sign out. Expired or rejected API access clears the shared session and returns to login. This is a browser-held bearer credential, not an HttpOnly cookie; close the tab or sign out on shared devices. Signing out clears the browser credential, not a copied token. Removing an email from the allowlist or rotating the protected management token revokes its API access.
- Authentication endpoints are `/api/admin/auth/capabilities`, `/request-code`, `/verify-code`, and authenticated `/session`. Existing `/api/knowledge/auth/request-code` and `/verify-code` aliases and existing signed sessions remain compatible. All administrator data APIs enforce access server-side; merely hiding a page is not the boundary. Deployed operations ingestion/routing still requires the independent OperationsWriter identity, and MCP still requires its dedicated read-only token. Operations browser updates use authenticated long polling without credentials in URLs.
- Development fixtures default to local administrator bypass. Set `AdministratorAccess__RequireSignIn=true` to require the full email login during the explicitly authorized local presentation; it is always required outside Development.
- The protected management token remains available to trusted deployment/administration scripts through `X-Knowledge-Token`, but is never requested by the browser. It also supplies domain-separated session-signing and challenge-hashing key material.
- MCP access token and management token must be different, cryptographically generated secrets of at least 32 characters.
- Keep `WorkItemChat__CreateEnabled=false` unless Boards creation has separately been authorized.

Use the Azure portal's protected App Service configuration to retrieve/rotate tokens. Never paste secrets into issues, code, chat, screenshots, or committed configuration. Rotating an App Service token also requires updating its corresponding GitHub development-environment secret.

Development deployment restores the email settings from `KNOWLEDGE_EMAIL_ENDPOINT`, `KNOWLEDGE_EMAIL_SENDER`, and `KNOWLEDGE_ADMIN_EMAILS` development-environment secrets. Keep those synchronized when changing delivery or approved users. Stored code challenges use a separate `authentication/` prefix, contain only keyed hashes and expiry/attempt metadata, and use ETag-conditional updates so concurrent verifications cannot consume a code twice. Sign-in responses are `no-store`; never log codes or session response bodies. ACS accepting an email is not evidence that it reached the inbox: verify receipt and sign-in with the administrator.

## VS Code

The repository's `.vscode/mcp.json` configures the `hotelKnowledge` HTTP server and prompts privately for the read-only token. Trust/start that server in VS Code, then verify that `list_knowledge`, `read_knowledge`, and `search_knowledge` are available.

## GitHub Copilot coding agent

The `hotel-developer` and `hotel-qa` profiles in `.github/agents/` include the hosted server in their `mcp-servers` frontmatter and explicitly allow only its three read tools. They reference the protected repository Agents secret; no token is embedded in either profile. This is GitHub's supported [agent-specific MCP configuration](https://docs.github.com/en/copilot/reference/custom-agents-configuration#mcp-server-configuration-details), so those agents do not require a separate repository-settings registration.

The profiles must be published to the Git revision used by the cloud agent before this configuration can take effect. VS Code ignores agent-profile MCP definitions and instead uses `.vscode/mcp.json`.

To make the server available outside those two profiles, optionally configure repository **Settings > Copilot > MCP servers**:

```json
{
  "mcpServers": {
    "hotelKnowledge": {
      "type": "http",
      "url": "https://ahb-dev-bj5rmi3w3ntgq-api.azurewebsites.net/mcp",
      "headers": {
        "Authorization": "Bearer $COPILOT_MCP_HOTEL_KNOWLEDGE_TOKEN"
      },
      "tools": ["list_knowledge", "read_knowledge", "search_knowledge"]
    }
  }
}
```

Create `COPILOT_MCP_HOTEL_KNOWLEDGE_TOKEN` as a repository **Agents** secret using the read-only MCP token. Do not supply the management token. GitHub has replaced the old `copilot` Actions-environment secret model with dedicated Agents secrets; new provisioning must use the Agents public key, not an Actions environment key. If the cloud-agent firewall blocks the service, allow only the fixed Azure API hostname. Confirm connectivity and tool discovery in GitHub before treating this configuration example as evidence of cloud-agent access.

Provisioning that secret alone does not register a server. The profiles above configure it for those custom agents; a repository administrator can additionally save the JSON in GitHub settings for repository-wide use. The public `GET /repos/{owner}/{repo}/copilot/cloud-agent/configuration` API inspects repository-wide registration, not agent-profile definitions, and has no documented configuration-write operation. Verify an actual agent retrieval before claiming GitHub Copilot consumption; a successful direct MCP request is not proof that a cloud agent has retrieved knowledge.

`scripts/Initialize-KnowledgeAccess.ps1` provisions distinct Azure tokens, development Actions secrets, and the read-only Agents secret without printing plaintext. Supply `-PythonExecutable` pointing to an isolated environment containing `PyNaCl==1.6.2`; it implements GitHub's required public-key sealed-box encryption. This is bootstrap tooling, not an application runtime dependency.

Agents first list or search, then read the relevant current document. Cite document path and revision. Stop when required evidence is missing, conflicting, pending approval, or unavailable; never silently substitute deleted repository copies.

## Management and recovery

All documents can be previewed, approved, and deleted, irrespective of source path. Delete requires confirmation and removes the retained original as well as search blocks. Rebuild requires confirmation, preserves document revisions and approvals, and uses the embedding deployment. A deleted document is not recreated from Git. To reintroduce deleted knowledge, explicitly add and approve it again.

If indexing fails after an upload, the pending durable document remains visible; repair the upstream failure, rebuild, and approve it. If deletion fails, retry that path or rebuild to prune the tombstoned source. Storage versioning and seven-day soft deletion permit administrator recovery, not automatic resurrection.

Provision storage with `infra/modules/knowledge-storage.bicep`. Archive the legacy center before removing repository copies:

```powershell
dotnet run --project tools\KnowledgeCenterMigration -- `
  --search-endpoint https://SEARCH.search.windows.net/ `
  --storage-endpoint https://STORAGE.blob.core.windows.net/
```

The archive verifies retained records, does not overwrite newer durable records, and does not modify the source index. Legacy PDF/Word originals cannot be recovered from extracted text. New uploads retain their actual bytes.

API requests and error examples are in `src/Api/knowledge.http`. Anonymous MCP/management access must fail; an MCP token must not authorize management. Use isolated fixtures for destructive checks, not live documents.
