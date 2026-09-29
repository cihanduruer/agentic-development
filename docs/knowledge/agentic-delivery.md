# Agentic delivery

## Flow

`Intake -> Triage -> Routed -> Development -> Code review -> QA -> Release ready -> Human approval -> Released`

Azure Boards stores work state. GitHub stores code, pull requests, checks, immutable build artifacts, and deployments.

## Jev decision contracts

| Decision | Allowed outputs |
|---|---|
| `next_worker` | live worker ID or `human_review` |
| `research_gate` | `accept`, `verify_more`, `reject` |
| `completion_gate` | `complete`, `verify_more`, `incomplete` |
| `action_guard` | `allow`, `confirm`, `human_review`, `deny` |

Jev receives labels and evidence metadata, not source code, secrets, personal data, or full internal documents. It begins in shadow mode. Activation requires measured routing accuracy, false-completion rate, latency, and cost plus a tested kill switch.

## Autonomy

- Read and analysis may run automatically.
- Draft and internal reversible writes may run within scoped permissions.
- External communication requires confirmation.
- Production, money, permanent deletion, and other irreversible actions require a human.

## Completion evidence

Completion requires acceptance criteria, output locations, test evidence, review status, knowledge revision, citations, and explicit gaps. An agent never grades its own research or implementation as the only reviewer.
