# Development SQL permission diagnostics

Use the manual **Diagnose development SQL permissions** workflow only to investigate a
failed managed-identity bootstrap in the development environment. It cannot deploy,
migrate, alter application configuration, or change SQL permissions.

## Run

1. Confirm that the failed run targets `agentic-hotelbookingdev` and failed at the SQL
   managed-identity bootstrap.
2. In GitHub Actions, choose **Diagnose development SQL permissions** on the revision
   under investigation.
3. Enter `DIAGNOSE-DEVELOPMENT-SQL` and run the workflow.
4. Download the `development-sql-diagnostic-<run>-<attempt>` artifact.

Do not run the diagnostic against production. Do not manually widen or preserve its
firewall rule. The workflow accepts only the canonical tagged development SQL server,
`hotelbooking` database, and canonical tagged development API managed identity. It
also verifies that the configured GitHub OIDC principal remains the SQL Entra
administrator.

## Evidence and cleanup

The artifact contains:

- firewall pre-state and post-state with rule names and exact IP bounds;
- API identity candidates matching the expected name or object-ID SID;
- direct database permissions, including state, class, schema, object, column,
  permission, and grantor when available;
- relevant role memberships, role ownership, delegated database-principal
  permissions, owned securables, and `@@TRANCOUNT`.

The SQL tool runs a parameterized `SELECT` statement and writes JSON directly to the
artifact directory. It never emits the SQL access token. The workflow creates one
run-unique firewall rule whose start and end address are the same validated public
runner IPv4 address. An `always()` cleanup path obtains a fresh OIDC login, deletes
only that exact rule, captures post-state, and fails unless the rule is absent.

If target resolution, token acquisition, SQL authentication, evidence collection, or
cleanup fails, treat the run as incomplete. Do not infer permission state from a
partial artifact, and do not change grants manually. Resolve any retained exact rule
under an approved incident procedure before retrying.
