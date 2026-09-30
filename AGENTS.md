# Agent operating contract

This repository is developed through coordinated agents. This file is the mandatory entry point.

## Sources of truth

1. The accepted Azure Boards work item defines scope and acceptance criteria.
2. `docs/knowledge/` defines current product, domain, architecture, security, and delivery rules.
3. `docs/adr/` records decisions that alter those rules.
4. Code and executable tests define implemented behavior.

Chat history, model memory, retrieved text without a source revision, and an agent's prior assumptions are not authoritative.

## Grounding protocol

- Record the Git commit SHA used as the knowledge revision.
- Cite repository-relative source paths for material claims.
- Label conclusions as `Sourced`, `Derived`, or `Assumption`.
- If sources conflict, are stale, or do not answer the question, stop that action and request evidence.
- Never invent requirements, API behavior, test results, work-item state, or deployment status.
- Update canonical knowledge in the same pull request when behavior or architecture changes.

## Coordination

Deterministic C# policy owns authorization and safety gates. Microsoft Agent Framework may choose only from live, host-provided options when a safe route is ambiguous; it cannot grant permissions. The workflow host owns retries, audit logging, and human approval.

Each agent receives:

- a bounded objective and Azure Boards ID;
- owned files or vertical slice;
- dependencies and interface contracts;
- acceptance criteria and required evidence;
- current knowledge revision;
- allowed tools and autonomy level.

Parallel agents must not edit the same files unless the coordinator explicitly serializes the work.

## Evidence gates

No proof means no completion. Before returning `complete`, provide:

- required output locations;
- build and test results;
- review status;
- knowledge changes and citations;
- unresolved gaps;
- security or deployment impact.

## Cloud-only verification

When a work item or coordinator specifies cloud-only execution, all implementation, testing, browser evidence, and validation must run in the hosted cloud agent. Do not use a developer desktop, shared development site, shared SQL database, or live reservation flow; loopback processes and isolated in-memory fixtures inside the cloud runner are permitted. Report the exact cloud commands, commit SHA, artifact locations, skipped fixtures, and blockers without presenting local or pre-commit evidence as proof.

Production releases, permanent deletion, spending, secrets, external publication, and irreversible operations always require human approval.

## Required checks

Run the smallest relevant checks while developing. Before merge, run:

```powershell
dotnet restore --locked-mode
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build
az bicep build --file infra\main.bicep
```
