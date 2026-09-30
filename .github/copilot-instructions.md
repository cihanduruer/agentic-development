# Copilot instructions

Follow [AGENTS.md](../AGENTS.md) and the canonical [delivery rules](../docs/knowledge/agentic-delivery.md).

## Default for new Hotel requirements

The product owner may be nontechnical. Accept requirements in plain language; do not require them to know work-item types, tags, GitHub commands, or agent prompts. Clarify missing business details when necessary and capture the agreed requirement with clear acceptance criteria.

Apply this default without requiring the user to repeat it:

> Create this as an Azure Boards User Story in `sample-project`. Leave it in `New` and do not add `github-synced`.

- Use organization `https://dev.azure.com/ai-enabled-ado-org` and project `sample-project`.
- At requirement capture, create the story only. Do not start coding, assign a development agent, create a GitHub implementation issue, manually dispatch intake, or mark the story `Active`.
- The existing scheduled intake owns subsequent synchronization, agent assignment, and state changes. Do not disable it or promise that the story will remain `New` indefinitely.
- Return the created story's link and a short, plain-language summary. If creation fails, explain the blocker instead of claiming success.
- Follow an explicit user override. Do not reset existing active work. This default applies to new Hotel product requirements, not questions or repository instruction maintenance.

## Execution

Application implementation and testing run through cloud agents and GitHub Actions, not developer-desktop agents or local fallback. Isolated loopback services inside a hosted cloud runner are allowed. A local exception requires an explicit user instruction.
