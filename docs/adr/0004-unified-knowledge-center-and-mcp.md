# ADR 0004: Unified durable Knowledge Center and hosted MCP

## Status

Accepted by the product owner on 2026-10-21. Supersedes repository-first knowledge, upload-only deletion, and origin-based source preference in ADR 0003.

## Decision

Knowledge Center is authoritative for application product, domain, and architecture knowledge. A document's historical path is an identifier, not a permission or trust category. All documents share approval, preview, deletion, and rebuild rules. There is no repository/upload partition in management or retrieval.

Azure Blob Storage holds private, versioned JSON document records containing extracted sections, revision, approval state, and original bytes for new files and pasted text. Azure Search is a disposable derivative. Legacy index-only documents retain extracted sections; missing binary originals are explicitly unavailable, never reconstructed or fabricated.

Management writes acquire a renewable cross-instance blob lease. Failed ingestion retains a pending durable document for repair. Deletion first writes a tombstone, then removes search blocks and the active stored document. A failed delete is not reported as success and can be retried. Storage versioning and seven-day soft deletion provide administrator recovery; there is no UI undo. Rebuild recreates a missing index schema, re-embeds retained documents, preserves revisions and approval, and prunes orphaned blocks. It never re-imports Git files or resurrects deleted documents.

The assistant validates retrieved blocks against current approved durable document revisions, so stale search blocks cannot override replacements or deletions. Pending documents are excluded regardless of their origin. Conflicting approved knowledge is reported instead of preferring a repository path.

The API hosts the official C# MCP SDK at `/mcp` using stateless Streamable HTTP. Its only tools are `list_knowledge`, `read_knowledge`, and `search_knowledge`. Listing and reading use canonical storage; hybrid search verifies canonical revisions. Results carry source path and revision, use bounded pages, and treat document text as reference data rather than executable agent instructions. An unavailable source is an explicit tool error, not permission to invent facts or use stale Git copies.

## Access and deployment

MCP requires a dedicated bearer token of at least 32 characters, including in Development. Hosted administration accepts either an approved administrator's eight-hour email-code session or a different `X-Knowledge-Token` for trusted scripts. Tokens are provisioned through protected Azure settings and GitHub development-environment secrets, never committed, returned by capabilities, or embedded in WebAssembly. The UI requests email codes at the shared `/admin/login`, not the static management secret. One session covers AI Product Management, Knowledge Center, architecture, operations, and their APIs; only Home and guest booking remain public application features. Legacy deep links cannot bypass the guard. Existing signed sessions and Knowledge Center code endpoint aliases remain compatible.

The session is remembered in `sessionStorage`; this intentionally avoids cross-site-cookie dependence between Static Web Apps and App Service, but is not an HttpOnly session and copied tokens survive browser sign-out until expiry or revocation. Codes expire after five minutes, have five guesses, are single-use and are conditionally consumed in durable storage. Delivery uses ACS Email with managed identity and a configured administrator allowlist. Development fixtures retain local bypass; the authorized local presentation sets `AdministratorAccess__RequireSignIn=true`. Operations dashboard reads and SignalR use administrator access (bearer headers with long polling); deployed machine ingestion/routing retains the distinct OperationsWriter policy. Neither administrator nor MCP credentials grant that machine role, and administrator sessions cannot authorize MCP.

The API identity requires Blob Data Contributor and Search Index Data Contributor. Search Service Contributor permits recreation of the fixed configured index schema; the API never accepts an arbitrary index or storage endpoint from a request. The storage account disables anonymous blobs and shared-key access. Deployment enables writes only after verifying that the hosted API enforces the token boundary.

`tools/KnowledgeCenterMigration` archives the existing index and verifies durable copies without modifying the index or overwriting existing records. The migration may import explicitly supplied repository files before their removal. No scheduled job copies deleted repository documents back into this center.

Git retains executable code, tests, delivery/security operating policy, and implementation ADRs. Repository product/domain/architecture Markdown copies are removed only after their durable records are verified. Agents cite Knowledge Center document revisions separately from the Git implementation revision.

## Consequences

MCP clients must be configured with a protected read-only token. VS Code configuration is supplied in `.vscode/mcp.json`. The Hotel GitHub custom-agent profiles configure the server through `mcp-servers` frontmatter and reference a dedicated repository Agents secret. Optional repository-wide MCP configuration belongs in repository Settings. Profile changes must be published to the revision used by a cloud agent, and an actual retrieval must be verified before claiming cloud-agent consumption.

Rebuild performs billable embedding calls and is a confirmed management action. Removing all documents is allowed; rebuilding an empty center yields an empty index. Model prompts cannot grant write access or restore deleted documents. A known document is not guaranteed to be relevant to a query; empty search results are not evidence of nonexistence.
