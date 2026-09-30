# Release runbook

1. Before requesting final QA, freeze the pull request title and body. Their metadata digest is part of the QA evidence identity; add later run and artifact links as pull-request comments, never by editing the frozen title or body.
2. Confirm every included Azure Boards item has acceptance criteria, pull request, knowledge revision, resolved blocking review findings, and a successful digest-bound `QA evidence` result.
3. Confirm `PR validation`, `Hotel code review`, and `QA evidence` are successful for the merged commit and frozen metadata, including locked restore, formatting, zero-warning build, tests, vulnerability scan, and Bicep validation.
4. Confirm the SQL Entra administrator is the deployment principal, the migration/bootstrap job succeeded, the API identity is only a member of `hotel_booking_runtime`, and the indexed knowledge revision matches the release commit.
5. Wait for `Release proposal` to succeed on `main`; record its run ID, full source commit SHA, QA metadata digest, artifact name, deployment-contract version, and SHA-256 manifest. Generate fresh digest-bound QA and release artifacts for every new promotion attempt.
6. Review database migration impact, operational risks, rollback plan, and the Azure what-if result when available.
7. In Actions, choose `Deploy production` on `main` and select **Run workflow**.
8. Enter the recorded release run ID and full commit SHA, type `DEPLOY-PRODUCTION`, separately type `DELETE-ACTIVE-LEGACY-SQL-SECRET`, and submit. Never substitute a branch name, tag, latest-run lookup, or rebuilt package.
9. The preflight job verifies the immutable selection, checksums, strictly typed current Entra SQL managed-identity deployment contract, both typed confirmations, QA run, and PR-validation result before the separate deployment job enters the `production` environment or authenticates to Azure. Before mutation, deployment requires an existing environment to contain exactly one SQL server whose Entra administrator is the GitHub deployment principal and records whether that server is already Entra-only. It then provisions shared infrastructure without changing existing SQL authentication or a configured live API and applies the fail-closed migration/bootstrap. An absent or identity-only API, including a partial-provisioning retry, is configured after bootstrap with the exact observed SQL server name. A configured upgrade instead deploys and proves the migration-disabled artifact while its prior connection remains unchanged. Cutover captures that connection, applies managed identity, and proves a SQL-backed request in one guarded step; failure restores and proves the prior connection. SQL-only enforcement remains blocked until readiness succeeds. Only then does `az sql server ad-only-auth enable` target the exact resource group/server from infrastructure outputs, without a parent-server redeployment. The existing `scripts/Get-AzureSqlAuthenticationMode.ps1` must read `entraOnly` from the authentication child resource before another catalog proof runs. Initial Bicep server creation remains Entra-only. Any earlier failure leaves the observed SQL authentication mode unchanged: a legacy-capable server remains legacy-capable, while initial and already-compliant servers remain Entra-only. Enable or readback failure blocks final catalog proof and credential cleanup even if the managed-identity API is healthy; inspect the child resource to establish the actual SQL mode rather than infer success or rollback. Never disable then re-enable protection or use SQL authentication as a fallback. It never grants itself Key Vault permissions; if metadata/delete access constrained to that secret is not pre-provisioned, an approved operator must delete the exact active secret and rerun the workflow so absence can be verified.
10. Run health, booking, telemetry, dashboard, Prompt Shields, and revision-grounding smoke tests.
11. On failure, stop promotion, select another currently eligible digest-bound release artifact through the same manual workflow, and update the Azure Boards item with evidence.
12. Mark work Released only after successful smoke tests and telemetry verification.

Artifacts created before metadata-digest binding or without the current deployment-contract manifest are read-only historical proof. They are not eligible for a new production promotion, including the historical `c6c4e0de0b4e8a21c0db561baea732fd42be73e2` showcase QA and release baseline.

The current `entra-sql-managed-identity` contract version is **2** (client-ID SQL SID
binding and scoped App Service identity resolution). Version-1 artifacts are also
ineligible, even when their older validator would accept them. Production preflight
uses policy from the trusted `main` dispatch revision; deployment source remains the
exact approved release commit.

Dedicated SQL Entra-only enforcement uses the existing classifier and infrastructure
outputs, so it adds no release-bundle capability and retains contract version 2.
Use a newly validated release selection for this correction; retaining version 2
does not retroactively prove enforcement for older bundles.

The `production` environment is restricted to `main`. Required reviewers and wait timers are not available for this private repository on the current GitHub billing plan (the settings API returned HTTP 422), so the environment does not currently provide a reviewer prompt. Branch protection and rulesets are also unavailable (HTTP 403). Repository write access, manual dispatch, exact typed confirmation, immutable run/SHA validation, direct revalidation of QA and PR-validation evidence, and checksums are mandatory compensating controls. A plan upgrade requires administrators to add environment reviewers and required checks before relying on GitHub settings as approval and merge gates.

Required `production` environment configuration:

- OIDC variables or secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
- Identity variables: `AZURE_SQL_ENTRA_ADMIN_NAME`, `AZURE_DEPLOYMENT_PRINCIPAL_OBJECT_ID`, and `OPERATIONS_API_AUDIENCE`, with the audience set to the API application client ID expected in Entra v2 access tokens.
- Azure federated credential restricted to this repository's `production` environment subject.
- The precreated `agentic-hotelbookingprod` resource group in West Europe.
- Contributor and Role Based Access Control Administrator for the GitHub deployment service principal, scoped only to `agentic-hotelbookingprod`, including permission for `Microsoft.Web/staticSites/listSecrets/action`.
- One-time SQL prerequisite: configure the environment's GitHub OIDC deployment principal as the logical server's Entra administrator if the identity cannot assign itself.

The production Static Web Apps deployment token is not a bootstrap secret. After Bicep creates or updates the site, the workflow obtains the token with the OIDC-authenticated Azure CLI, masks it, and passes it directly to the deployment action. It is neither logged nor stored as a GitHub secret.

Never run the production workflow to test pipeline syntax or credentials. Validate syntax in pull requests and use development for deployment tests.
