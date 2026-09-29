# ADR 0001: Agentic delivery platform

**Status:** Superseded by ADR 0002
**Date:** 2026-09-29

## Decision

Use GitHub Copilot agents for software work, Microsoft Agent Framework as workflow host, Jev for bounded typed routing decisions, Azure Boards for task visibility, GitHub Actions for build and deployment, and Azure for runtime services.

Canonical knowledge remains versioned in GitHub and is projected into Azure AI Search. Foundry evaluation and Azure AI Content Safety provide quality and safety signals. Signals do not replace host authorization or human approval.

## Consequences

Routing is cheap and observable, workers remain replaceable, and irreversible actions stay human-controlled. Jev is an external dependency and therefore receives minimized metadata, starts in shadow mode, and requires a deterministic bypass.
