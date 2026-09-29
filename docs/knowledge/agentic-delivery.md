# Agentic delivery

## Flow

`Intake -> Triage -> Routed -> Development -> Code review -> QA -> Release ready -> Human approval -> Released`

Azure Boards stores work state. GitHub stores code, pull requests, checks, immutable build artifacts, and deployments.

## Azure Boards intake

The `Agentic intake` workflow starts work from Azure Boards in two modes:

- Scheduled runs query only `New` User Stories and Bugs without the `github-synced` tag.
- Manual runs require one Azure Boards work-item ID and process only that item. This is the recovery path for a partially completed intake.

`scripts/Start-AgenticWork.ps1` uses `AB#<id>` in the GitHub issue title or canonical Azure Boards link as the idempotency key. It resumes the single matching issue instead of creating a duplicate and fails closed if multiple issues match. It assigns Copilot through GitHub's public-preview issues REST API, preserving existing assignees when repairing an existing issue. The `COPILOT_AGENT_TOKEN` secret must be a GitHub user token; a classic token requires `repo`, while a fine-grained token requires metadata read and actions, contents, issues, and pull requests read/write access. The workflow's `GITHUB_TOKEN` is not used for Copilot assignment.

GitHub can represent the assigned agent as the documented `copilot-swe-agent`/`copilot-swe-agent[bot]` login or as the `Copilot` bot projection. Intake validates the stable bot identity first and accepts those documented login forms. Only after assignment is verified does it move a `New` Board item to `Active`, add `github-synced`, append the GitHub issue hyperlink, and record history. Re-running an already synchronized item does not duplicate the issue or Board link.

If GitHub marks a Copilot-authored pull request run `action_required` before creating jobs, an authorized maintainer can dispatch `PR validation` with the pull request number and its full current head SHA. The workflow reads the pull request through GitHub's API, verifies that it targets this repository's `main`, originates in this repository, and still has that exact head SHA before checkout. This provides exact-commit evidence without using `pull_request_target` to execute untrusted pull-request code.

## Decision contracts

| Decision | Allowed outputs |
|---|---|
| `next_worker` | live worker ID or `human_review` |
| `research_gate` | `accept`, `verify_more`, `reject` |
| `completion_gate` | `complete`, `verify_more`, `incomplete` |
| `action_guard` | `allow`, `confirm`, `human_review`, `deny` |

Deterministic policy handles incomplete evidence, high-risk actions, irreversible actions, and single eligible workers without a model call. Microsoft Agent Framework invokes Azure OpenAI only when multiple safe workers are eligible. Structured output is validated against the live menu, and low confidence, invalid output, configuration failure, or service failure routes to `human_review`.

The model receives labels and evidence metadata, not source code, prompts, secrets, personal data, work-item descriptions, or full internal documents. Application Insights records latency and failures; the operations event stream records the effective route, confidence, policy/model identifier, outcome, and knowledge revision.

## Autonomy

- Read and analysis may run automatically.
- Draft and internal reversible writes may run within scoped permissions.
- External communication requires confirmation.
- Production, money, permanent deletion, and other irreversible actions require a human.

## Completion evidence

Completion requires acceptance criteria, output locations, test evidence, review status, knowledge revision, citations, and explicit gaps. An agent never grades its own research or implementation as the only reviewer.
