---
owner: Engineering owner
last_reviewed: 2026-09-30
---
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

GitHub intentionally marks Copilot cloud-agent pull request runs `action_required` before creating jobs when **Require approval for workflow runs** is enabled under the repository's Copilot cloud-agent settings. A maintainer can approve the run from the pull request merge box, or dispatch `PR validation` from `main` with the pull request number and its full current head SHA. The manual workflow uses the trusted default-branch definition, reads the pull request through GitHub's API, verifies that it is open, targets this repository's `main`, originates in this repository, and still has that exact head SHA, then checks out only that SHA and asserts the checked-out commit before producing provenance. Pull-request-triggered runs execute PR-modifiable workflow content and are not accepted as trusted QA provenance. This provides exact-commit evidence without using `pull_request_target` to execute untrusted pull-request code. Administrators may disable the Copilot-specific approval setting when repository policy permits, but automation does not change that security setting.

## Azure Boards delivery synchronization

After a successful development deployment, lifecycle synchronization requires one merged same-repository PR, one trusted canonical `AB#<id>` issue, exact-deployed-SHA validation and QA evidence, and exact-head Copilot review evidence. The delivery identity must be in the PR title or its `Azure Boards:` tracking field; incidental body references are ignored. Automatic runs wait for bounded evidence races; manual replay pins the deployment run, SHA, and PR. PR metadata is parsed as a complete set: missing, invalid, or contradictory platform declarations, Azure Boards N/A combined with an AB identity, and incomplete GitHub issue-search results fail closed. An unambiguous `Platform change: true` without an AB identity skips lifecycle synchronization; `Azure Boards: N/A` makes that platform classification explicit even when source or test files change.

Synchronization never changes work-item state. It adds `delivery-evidence`, removes `ready-for-triage`, and adds deduplicated PR, deployment, validation, QA, and review links under an optimistic revision test. Complete replays are no-ops; terminal items may receive missing evidence but are never reopened.

## Decision contracts

| Decision | Allowed outputs |
|---|---|
| `next_worker` | live worker ID or `human_review` |
| `research_gate` | `accept`, `verify_more`, `reject` |
| `completion_gate` | `complete`, `verify_more`, `incomplete` |
| `action_guard` | `allow`, `confirm`, `human_review`, `deny` |

Deterministic policy immediately routes incomplete evidence, high-risk actions, and irreversible actions to `human_review`. Every otherwise-eligible request invokes Azure AI Content Safety Prompt Shields and requires Azure AI Search grounding for the exact knowledge revision. Microsoft Agent Framework invokes Azure OpenAI only when those checks pass and multiple safe workers are eligible. Structured output is validated against the live menu. Prompt attack detection, absent revision grounding, malformed evaluation responses, low confidence, invalid output, configuration failure, or any evaluation/model service failure routes to `human_review`.

The model receives labels and evidence metadata, not source code, prompts, secrets, personal data, work-item descriptions, or full internal documents. Application Insights records latency and failures; the operations event stream records the effective route, confidence, policy/model identifier, outcome, and knowledge revision.

## Supported routing capability vocabulary

The development deployment smoke verifies the deterministic `qa-agent` route with task category `quality-assurance` and required capability `api-testing`. This canonical section intentionally keeps the searchable quality assurance API testing terms together so the exact-revision Azure AI Search grounding gate has explicit evidence for that supported route.

## Autonomy

- Read and analysis may run automatically.
- Draft and internal reversible writes may run within scoped permissions.
- External communication requires confirmation.
- Production, money, permanent deletion, and other irreversible actions require a human.

## Completion evidence

Completion requires acceptance criteria, output locations, test evidence, review status, knowledge revision, citations, and explicit gaps. An agent never grades its own research or implementation as the only reviewer.

## Automated review, QA, and release gates

Eligible non-draft pull requests to `main` that change application, test, infrastructure, delivery-script, agent, or workflow paths request `copilot-pull-request-reviewer[bot]` through GitHub's supported review-request API. The gate waits for a Copilot review of the current head commit. Unresolved findings explicitly labeled High are blocking. Findings whose severity cannot be read from the API response also block rather than being silently downgraded. A blocking result applies `development-required`; development must resolve the thread and trigger a new review. Medium and Low findings remain visible but do not block this gate.

After review and trusted manual PR validation, `.github/workflows/qa-evidence.yml` resolves the immutable pull-request head recorded by the triggering review run, requires an unexpired exact-SHA validation artifact from a successful `pr-validation.yml` dispatch on `main`, independently rebuilds and reruns its tests, compiles Bicep, checks acceptance-criteria and negative-path evidence in the pull request, and uploads an auditable result. Product-versus-platform QA classification uses the same fail-closed PR metadata contract as lifecycle synchronization rather than changed paths alone, so an explicit platform/N/A change may truthfully record product evidence as not applicable even when it hardens `src/` or `tests/`, while every non-platform change requires one AB identity. The QA artifact name binds both the exact head SHA and a SHA-256 digest of the classified pull-request title and body; lifecycle synchronization recomputes both values so mutable metadata invalidates stale provenance. A changed or unresolvable pull-request head fails closed. GitHub does not provide a supported pull-request check API for dispatching the repository's `hotel-qa` custom agent. The workflow therefore records `hotel-qa` execution as `not-run`; it enforces that profile's evidence contract without claiming an agent ran. A human may invoke `hotel-qa` separately when agent judgment is required.

After the same QA gate succeeds for a commit on `main`, `.github/workflows/release-proposal.yml` publishes the API and web exactly once and uploads their SHA-256 checksums with the source commit, QA run identity, and versioned deployment-contract manifest. A source commit without the current Entra SQL managed-identity contract cannot produce an eligible artifact. It proposes a release; it does not deploy.

The current `entra-sql-managed-identity` deployment contract is version **2**, requiring the scoped App Service identity resolver and explicit client-ID SQL bootstrap capability. Version-1 releases remain historical evidence and cannot be promoted by the current production workflow. Its verification job checks out the trusted `main` dispatch commit (`github.sha`) for contract policy, never the operator-selected ancestor's older validator. The deployment job still checks out only the exact approved artifact source (`inputs.commit_sha`); source ancestry, checksums, QA, approvals and target-SHA checks are unchanged. This rejects incompatible releases before entering the production environment or authenticating to Azure.

`.github/workflows/deploy-production.yml` is manual-only. It accepts an exact successful release workflow run ID and full `main` commit SHA, requires the typed confirmations `DEPLOY-PRODUCTION` and `DELETE-ACTIVE-LEGACY-SQL-SECRET`, verifies the run and commit through GitHub's API, downloads the named artifact from that run, strictly verifies every checksum, JSON deployment-contract type/version, and its QA and PR-validation provenance, and only then enters the `production` GitHub Environment in a separate deployment job. This makes an older release whose selected commit lacks the current migration/bootstrap contract ineligible before Azure authentication. It deploys `infra/resource-group.bicep` within the precreated `agentic-hotelbookingprod` resource-group scope and promotes the verified binaries without rebuilding. Active legacy credential cleanup occurs only after SQL bootstrap, API deployment, API health, and a SQL-backed catalog request succeed. The OIDC-authenticated deployment identity obtains and masks the production Static Web Apps deployment token only after infrastructure exists, avoiding a circular bootstrap secret. Production deployment is never triggered by a push, merge, schedule, or successful check.

Development and production classify SQL before any deployment mutation, including whether an existing server is already Entra-only. Initial-empty environments may create Entra-only SQL immediately because no live password-backed API exists. Existing environments must expose exactly one SQL server and must already configure the GitHub deployment principal as Entra administrator; otherwise deployment fails before mutation. The first Bicep phase excludes existing SQL and disables API configuration, then independently classifies the API runtime setting. An absent or identity-only API remains on the initial path even when a previous failed attempt already created SQL. After database migration and exact-role bootstrap, that path configures the API before artifact deployment because no configured service exists. A configured upgrade instead deploys and proves the migration-disabled artifact while the prior app connection remains unchanged. The cutover captures that connection, applies the managed-identity setting, and proves a SQL-backed request as one guarded operation; failure restores and proves the prior connection before stopping. Only proven managed-identity readiness permits the SQL-only Entra phase, followed by another SQL-backed proof. Migration, bootstrap, API configuration, or readiness failure therefore cannot disable legacy SQL authentication; if SQL-only enforcement reports a partial failure, the already-proven managed-identity API remains the safe runtime state and credential cleanup stays blocked pending verification.

Development routing readiness retries the authenticated exact-revision request for up to ten minutes while Content Safety and Search data-plane RBAC propagate. Only HTTP 200 `human_review` decisions whose production reason ends with the parsed code `(evaluation_error)` are transient; authentication failures, malformed responses, guardrail rejections, synthetic bare codes, and other decisions fail immediately. Each retry intentionally persists another route event with the same deployment-readiness correlation ID, representing one bounded readiness series rather than independent business decisions. Success requires the expected deterministic `qa-agent` decision before the overall deadline, and terminal diagnostics record only the final status, worker, model, reason, revision, correlation, and attempt count without tokens.

The current private-repository billing plan does not support branch protection, rulesets, environment reviewers, or environment wait timers; the relevant GitHub APIs return HTTP 403 or 422. These controls therefore cannot be claimed as enforced. The production workflow compensates by revalidating successful QA and PR-validation evidence for the exact selected commit before Azure authentication, while manual dispatch, typed confirmation, `main` containment, artifact checksums, and environment branch restriction remain mandatory.

## Grounded evaluation evidence

PR validation persists deterministic gate and indexer test results. `.github/workflows/grounded-evaluation.yml` is the explicit live-cloud evaluation path: it authenticates with GitHub OIDC, invokes the Microsoft `azure.ai.evaluation.GroundednessEvaluator`, fails below a score of 4, and uploads revision-named JSON evidence. This workflow is manual because each invocation consumes the configured model deployment; a passing local/unit suite is not represented as a live Foundry evaluation.
