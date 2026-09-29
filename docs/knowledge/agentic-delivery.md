# Agentic delivery

## Flow

`Intake -> Triage -> Routed -> Development -> Code review -> QA -> Release ready -> Human approval -> Released`

Azure Boards stores work state. GitHub stores code, pull requests, checks, immutable build artifacts, and deployments.

## Azure Boards intake

The `Agentic intake` workflow starts work from Azure Boards in two modes:

- Scheduled runs query only `New` User Stories and Bugs without the `github-synced` tag.
- Manual runs require one Azure Boards work-item ID and process only that item. This is the recovery path for a partially completed intake.

`scripts/Start-AgenticWork.ps1` considers only issues whose GitHub author association is `OWNER`, `MEMBER`, or `COLLABORATOR`, then uses `AB#<id>` in the title or an exact canonical Azure Boards organization/project/item link as the idempotency key. It resumes the single matching issue instead of creating a duplicate and fails closed if multiple issues match. Manual recovery accepts only `New` or `Active` items so it cannot restart terminal work. It assigns Copilot through GitHub's public-preview issues REST API, preserving existing assignees when repairing an existing issue. The `COPILOT_AGENT_TOKEN` secret must be a GitHub user token; a classic token requires `repo`, while a fine-grained token requires metadata read and actions, contents, issues, and pull requests read/write access. The workflow's `GITHUB_TOKEN` is not used for Copilot assignment.

GitHub can represent the assigned agent as the documented `copilot-swe-agent`/`copilot-swe-agent[bot]` login or as the `Copilot` bot projection. Intake validates the stable bot identity first and accepts those documented login forms. Only after assignment is verified does it move a `New` Board item to `Active`, add `github-synced`, append the GitHub issue hyperlink, and record history. Re-running an already synchronized item does not duplicate the issue or Board link.

GitHub intentionally marks Copilot cloud-agent pull request runs `action_required` before creating jobs when **Require approval for workflow runs** is enabled under the repository's Copilot cloud-agent settings. A maintainer can approve the run from the pull request merge box, or dispatch `PR validation` with the pull request number and its full current head SHA. The manual workflow reads the pull request through GitHub's API, verifies that it targets this repository's `main`, originates in this repository, and still has that exact head SHA before checkout. This provides exact-commit evidence without using `pull_request_target` to execute untrusted pull-request code. Administrators may disable the Copilot-specific approval setting when repository policy permits, but automation does not change that security setting.

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

## Automated review, QA, and release gates

Eligible non-draft pull requests to `main` that change application, test, infrastructure, delivery-script, agent, or workflow paths request `copilot-pull-request-reviewer[bot]` through GitHub's supported review-request API. The gate waits for a Copilot review of the current head commit. Unresolved findings explicitly labeled High are blocking. Findings whose severity cannot be read from the API response also block rather than being silently downgraded. A blocking result applies `development-required`; development must resolve the thread and trigger a new review. Medium and Low findings remain visible but do not block this gate.

After review and PR validation, `.github/workflows/qa-evidence.yml` independently rebuilds and reruns the tests, compiles Bicep, checks acceptance-criteria and negative-path evidence in the pull request, and uploads an auditable result. GitHub does not provide a supported pull-request check API for dispatching the repository's `hotel-qa` custom agent. The workflow therefore records `hotel-qa` execution as `not-run`; it enforces that profile's evidence contract without claiming an agent ran. A human may invoke `hotel-qa` separately when agent judgment is required.

After the same QA gate succeeds for a commit on `main`, `.github/workflows/release-proposal.yml` publishes the API and web exactly once and uploads their SHA-256 checksums with the source commit and QA run identity. It proposes a release; it does not deploy.

`.github/workflows/deploy-production.yml` is manual-only. It accepts an exact successful release workflow run ID and full `main` commit SHA, requires the typed confirmation `DEPLOY-PRODUCTION`, verifies the run and commit through GitHub's API, downloads the named artifact from that run, verifies every checksum, and then enters the `production` GitHub Environment. It deploys `infra/production.parameters.json` to the separate `agentic-hotelbookingprod` resource group and promotes the verified binaries without rebuilding. The OIDC-authenticated deployment identity obtains and masks the production Static Web Apps deployment token only after infrastructure exists, avoiding a circular bootstrap secret. Production deployment is never triggered by a push, merge, schedule, or successful check.

The current private-repository billing plan does not support branch protection, rulesets, environment reviewers, or environment wait timers; the relevant GitHub APIs return HTTP 403 or 422. These controls therefore cannot be claimed as enforced. The production workflow compensates by revalidating successful QA and PR-validation evidence for the exact selected commit before Azure authentication, while manual dispatch, typed confirmation, `main` containment, artifact checksums, and environment branch restriction remain mandatory.
