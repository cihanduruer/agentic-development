---
owner: Engineering owner
last_reviewed: 2026-09-30
---
# Knowledge center

These documents are the canonical context for people and agents.

| Document | Purpose | Owner | Review cadence |
|---|---|---|---|
| [product.md](product.md) | Product scope and user outcomes | Product owner | Every feature |
| [domain.md](domain.md) | Hotel-booking language and rules | Domain owner | Every rule change |
| [architecture.md](architecture.md) | Components, integrations, and constraints | Architecture owner | Every ADR |
| [agentic-delivery.md](agentic-delivery.md) | Agent routing, gates, and autonomy | Engineering owner | Monthly |
| [security.md](security.md) | Identity, data, and action controls | Security owner | Monthly |

Every indexed chunk must carry its repository path, commit SHA, document owner, and last-reviewed date.
Azure AI Search is a retrieval projection; this directory remains authoritative.

## Using knowledge in the demo

Start with the [short team demo](../../demo/TEAM-DEMO-GUIDE.md). Agents can read the repository documents now; that alone is not evidence of a call to Azure AI Search.

Developers and architects can use the [Azure service responsibility map and harness diagrams](../development-pipeline/AZURE-SERVICES-AND-HARNESS.md) to distinguish software delivery, runtime routing, identity, and knowledge retrieval.

The read-only knowledge MCP integration is being implemented separately. Until it is deployed, configured in the actual client, and verified with a real tool call, describe Search-backed chat and development-agent retrieval as pending, not connected. Demonstrate chat retrieval first, then verify development-agent retrieval separately; one client's success does not prove the other's connection.

A demonstrated Search answer must identify the requested revision, retrieved passage, repository path, and source link. No matching evidence means an explicit gap, not an invented answer. A local documentation commit does not refresh the Search projection: publish and index the intended revision through the approved cloud path before expecting it in retrieval results.
