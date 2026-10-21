# ADR 0003: Product assistant and knowledge center on the admin page

**Status:** Accepted  
**Date:** 2026-10-14  
**Extends:** ADR 0002 (Azure OpenAI through managed identity)

**Partially superseded:** [ADR 0004](0004-unified-knowledge-center-and-mcp.md) replaces repository-first authority, origin-based preference, upload-only deletion, original-file retention, and knowledge management access rules below. These paragraphs retain the historical decision, not current implementation policy.

## Decision

`/admin` is an AI Product Management assistant. A product manager describes a requirement in plain language; the assistant retrieves grounded context from the `hotel-knowledge-center` Azure AI Search index (hybrid vector and keyword with semantic ranking, 3,072-dimension `text-embedding-3-large` embeddings) and drafts an Azure Boards User Story with acceptance criteria. The chat model is the `gpt-6.1-sol` deployment. The model only drafts; it never calls Boards.

Creation is a separate, explicit step: `POST /api/work-items` re-validates the draft and creates a `User Story` in `ai-enabled-ado-org/sample-project`. It starts in `New` without `github-synced`, so the existing scheduled intake owns every later transition. The repository's capture contract in `agentic-delivery.md` is unchanged.

The repository `docs/knowledge/*.md` files remain the canonical source for the Copilot cloud agent. The knowledge center is an additional, searchable copy of the same files (migrated by `tools/KnowledgeCenterMigration`) plus content that product managers add.

Product managers can upload PDF, Word, Excel, text, Markdown or CSV files, or paste text. Each upload starts as pending approval and is excluded from retrieval until a person selects Approve. Re-uploading a file replaces its blocks and resets it to pending. Only `uploads/` sources can be approved or removed.

## Safety controls

- `WorkItemChat__CreateEnabled` and `WorkItemChat__UploadEnabled` default to false. When false, work-item creation, uploads, approval, and removal return HTTP 403. Infrastructure sets the upload setting to false and grants the API identity only Search Index Data Reader, so uploads work only in local or otherwise explicitly authorized environments. Creation is not set in infrastructure.
- Uploads are limited to 5 MB and verified by file signature, package contents, ZIP entry and expansion limits, page and row limits, and extracted-text limits. Executables and unsupported types are rejected.
- The prompt tells the model to treat retrieved text as reference data, ignore instructions in it, and prefer repository documentation over uploaded content.
- The API reaches Azure OpenAI and Azure AI Search only through its managed identity.

## Consequences

The knowledge management UI is separated from the assistant at `/admin/knowledge`, with a searchable source hierarchy and adjacent document viewer. Users can preview complete indexed text for repository and uploaded sources, or switch to individual indexed blocks. Markdown is formatted server-side with raw HTML disabled and allowlist sanitization. Original binary files are not retained by the search index: PDF sources explicitly fall back to indexed text, and an original PDF can be selected locally for sandboxed browser viewing without another upload. Upload deletion requires confirmation, removes the selected source's indexed blocks only, and remains subject to the existing disabled-by-default write gate. Repository-backed knowledge cannot be deleted through this UI or API, and original files are never deleted. These changes do not add sign-in or broaden the deployed API's write permissions.

`/admin`, `/api/work-item-chat`, and `/api/knowledge/blocks` have no sign-in or authorization. Anyone who can reach the API can send chat messages that consume model capacity and can read indexed knowledge. They cannot create work items, upload, approve, or remove content unless the creation and upload settings are explicitly enabled; do not enable those settings on a reachable environment until sign-in exists. Approval is a convention without identity: the index records neither who approved nor when, and the `Owner` field holds the pending or approved status.

Index content is knowledge, not secrets. Do not upload credentials, personal data, or confidential material. A future decision should add Entra sign-in for the admin page and the assistant endpoints, an approver identity, and rate limits.
