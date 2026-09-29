# Release runbook

1. Before requesting final QA, freeze the pull request title and body. Their metadata digest is part of the QA evidence identity; add later run and artifact links as pull-request comments, never by editing the frozen title or body.
2. Confirm every included Azure Boards item has acceptance criteria, pull request, knowledge revision, resolved blocking review findings, and a successful digest-bound `QA evidence` result.
3. Confirm `PR validation`, `Hotel code review`, and `QA evidence` are successful for the merged commit and frozen metadata.
4. Wait for `Release proposal` to succeed on `main`; record its run ID, full source commit SHA, QA metadata digest, artifact name, and SHA-256 manifest. Generate fresh digest-bound QA and release artifacts for every new promotion attempt.
5. Review database migration impact, operational risks, rollback plan, and the Azure what-if result when available.
6. In Actions, choose `Deploy production` on `main` and select **Run workflow**.
7. Enter the recorded release run ID and full commit SHA, type `DEPLOY-PRODUCTION`, and submit. Never substitute a branch name, tag, latest-run lookup, or rebuilt package.
8. The preflight job verifies the immutable selection, checksums, QA run, and PR-validation result before the separate deployment job enters the `production` environment or authenticates to Azure. The deployment job uses the separate production parameters/resource group and promotes the API and web artifacts without rebuilding.
9. Run health, booking, telemetry, and dashboard smoke tests.
10. On failure, stop promotion, select another currently eligible digest-bound release artifact through the same manual workflow, and update the Azure Boards item with evidence.
11. Mark work Released only after successful smoke tests and telemetry verification.

Artifacts created before metadata-digest binding are read-only historical proof. They are not eligible for a new production promotion, including the historical `c6c4e0de0b4e8a21c0db561baea732fd42be73e2` showcase QA and release baseline.

The `production` environment is restricted to `main`. Required reviewers and wait timers are not available for this private repository on the current GitHub billing plan (the settings API returned HTTP 422), so the environment does not currently provide a reviewer prompt. Branch protection and rulesets are also unavailable (HTTP 403). Repository write access, manual dispatch, exact typed confirmation, immutable run/SHA validation, direct revalidation of QA and PR-validation evidence, and checksums are mandatory compensating controls. A plan upgrade requires administrators to add environment reviewers and required checks before relying on GitHub settings as approval and merge gates.

Required `production` environment configuration:

- OIDC variables or secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
- Environment variable: `OPERATIONS_API_AUDIENCE` set to the API application client ID expected in Entra v2 access tokens.
- Secret: `SQL_ADMIN_PASSWORD`.
- Azure federated credential restricted to this repository's `production` environment subject.
- The precreated `agentic-hotelbookingprod` resource group in West Europe.
- Contributor and Role Based Access Control Administrator for the GitHub deployment service principal, scoped only to `agentic-hotelbookingprod`, including permission for `Microsoft.Web/staticSites/listSecrets/action`.

The production Static Web Apps deployment token is not a bootstrap secret. After Bicep creates or updates the site, the workflow obtains the token with the OIDC-authenticated Azure CLI, masks it, and passes it directly to the deployment action. It is neither logged nor stored as a GitHub secret.

Never run the production workflow to test pipeline syntax or credentials. Validate syntax in pull requests and use development for deployment tests.
