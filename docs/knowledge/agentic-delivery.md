# Agentic delivery

## Flow

`Intake -> Triage -> Routed -> Development -> Code review -> QA -> Release ready -> Human approval -> Released`

Azure Boards stores work state. GitHub stores code, pull requests, checks, immutable build artifacts, and deployments.

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
