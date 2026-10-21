# Agent operating contract

This repository is developed through coordinated agents. This file is the mandatory entry point.

## Sources of truth

1. The accepted Azure Boards work item defines scope and acceptance criteria.
2. Knowledge Center defines current product, domain, and application architecture knowledge. Read it through the `hotelKnowledge` MCP tools; see `docs/runbooks/knowledge-center-mcp.md`. Repository `docs/knowledge/` retains engineering security and delivery operating policy, not a competing product knowledge source.
3. `docs/adr/` records decisions that alter those rules.
4. Code and executable tests define implemented behavior.

Chat history, model memory, retrieved text without a source revision, and an agent's prior assumptions are not authoritative.

## Grounding protocol

- Record the Git implementation SHA and the Knowledge Center document revisions used as evidence.
- Cite Knowledge Center document paths and revisions for application knowledge; cite repository-relative paths for code and engineering policy.
- Label conclusions as `Sourced`, `Derived`, or `Assumption`.
- If sources conflict, are stale, or do not answer the question, stop that action and request evidence.
- If Knowledge Center or MCP is unavailable, report the blocker. Do not silently use deleted repository copies or the legacy standalone repository-index MCP service.
- Never invent requirements, API behavior, test results, work-item state, or deployment status.
- Update canonical Knowledge Center knowledge when behavior changes and record the verified document revisions in the pull request; update implementation ADRs and engineering policy in the same pull request when relevant.

## Coordination

Newly started project coordinator, developer, and QA agents must use GPT 6.1 Sol (`gpt-6.1-sol`) through actual host model selection, not prompt instructions alone. If the model is unavailable, report the error instead of silently substituting another model. Running sessions do not switch models mid-session. This policy does not control GitHub's separately managed review bot.
Use `gpt-6.1-sol` (GPT 6.1 Sol) for newly started project agents, including coordinators, development agents, and QA agents. Configure the host's actual model-selection setting; prose alone does not select a model. If the requested model is unavailable, report the blocker rather than silently substituting another model. Existing sessions do not switch models mid-session.

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
