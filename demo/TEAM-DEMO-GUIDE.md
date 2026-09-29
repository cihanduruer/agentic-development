# Agentic Development Team Demo

## 1. Open the repository

- Open the repository in GitHub Copilot Desktop.
- Check out the latest `main` branch.

## 2. Refine a product requirement

Ask Copilot:

> Read `AGENTS.md` and `docs/knowledge/`. Help me refine a Hotel Web/API requirement into one bounded vertical slice with testable acceptance criteria.

Discuss the requirement until the user outcome, failure behavior, and acceptance criteria are clear.

## 3. Create the work item

Ask Copilot:

> Create this as an Azure Boards User Story in `sample-project`. Leave it in `New` and do not add `github-synced`.

Use a Bug instead of a User Story when correcting existing behavior.

## 4. Show automatic intake

Within five minutes, GitHub Actions:

1. Finds the new Azure Boards item.
2. Creates a linked GitHub issue.
3. Assigns the Hotel developer agent.
4. Moves the Boards item to `Active`.
5. Adds `github-synced` to prevent duplicate dispatch.

Do not manually create a competing GitHub issue, branch, or agent session.

## 5. Show agent implementation

The developer agent:

1. Reads the shared repository knowledge.
2. Creates a branch.
3. Implements one vertical slice.
4. Adds focused tests.
5. Opens a pull request linked with `AB#<id>`.

## 6. Show quality gates

- GitHub Actions validates formatting, build, tests, security contracts, and infrastructure.
- Copilot reviews the exact pull-request revision.
- Review findings are fixed and revalidated.
- Independent QA verifies acceptance criteria and negative paths.

## 7. Approve delivery

- Review the pull request, CI results, review findings, and QA evidence.
- A human approves the merge or release.
- A bot recommendation is not human approval.

## 8. Show deployment and traceability

- Merge the approved pull request.
- Development deploys automatically.
- Verify the Hotel Web application and API.
- Show the evidence chain:
  `Azure Boards -> GitHub issue -> branch -> pull request -> CI/review/QA -> deployment`.

## 9. Explain the boundary

- **Product:** Hotel Web UI and .NET API.
- **Delivery system:** agents, Azure Boards, GitHub Actions, review, QA, and deployment automation.
- **Production:** manual only and requires human approval.
