---
owner: Engineering owner
last_reviewed: 2026-10-21
---
# Knowledge authority

Product, domain, and application architecture documents live in the authoritative Knowledge Center. Historical source paths remain stable document identifiers, not files to recreate in Git.

Use `hotelKnowledge` MCP to list, search, and read current approved revisions. See the [connection and recovery runbook](../runbooks/knowledge-center-mcp.md) and [ADR 0004](../adr/0004-unified-knowledge-center-and-mcp.md).

Repository product/domain/architecture copies were removed only after their original bytes and extracted sections were verified in versioned private storage. Rebuild operates on retained documents, never this directory.

This directory retains [security](security.md), [delivery](agentic-delivery.md), and other engineering operating policies. Those policies and executable code are not a competing application knowledge copy. If MCP is unavailable, report missing evidence rather than falling back to historical Git documents. Record the implementation SHA separately from cited Knowledge Center document revisions.
