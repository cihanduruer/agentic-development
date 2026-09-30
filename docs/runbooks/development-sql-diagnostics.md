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
- safe ARM identity binding (`managed-identity.json`) with separate principal object ID,
  application/client ID, tenant ID, and exact App Service resource ID;
- SQL target fields `principalObjectId`, `principalClientId`, and `expectedSid` (the
  client ID in SQL binary representation);
- API identity candidates matching the expected name, client-ID SID, or historical
  object-ID SID; each candidate's `sid` is observed binary hex and `sidGuid` is its
  GUID interpretation, not a claim that the SID is an object ID;
- direct database permissions for those candidates and `hotel_booking_runtime`,
  including state, class, schema, object, column, permission, and grantor when
  available;
- relevant role memberships, role ownership, delegated database-principal
  permissions, owned securables, observer metadata visibility, and `@@TRANCOUNT`.

The SQL tool requests `ApplicationIntent=ReadOnly`, runs a fixed parameterized
`SELECT` statement, and writes JSON directly to the artifact directory. Application
intent is defense in depth, not an authorization boundary; the command itself remains
free of DDL, DML, permission changes, dynamic SQL, and user-supplied SQL. It never
emits the SQL access token. The workflow creates one run-unique firewall rule whose
start and end address are the same validated public runner IPv4 address. An
`always()` cleanup path obtains a fresh OIDC login, deletes only that exact rule,
captures post-state, and fails unless the rule is absent.

`@@TRANCOUNT` describes only the diagnostic connection's current session. It cannot
prove whether a prior deployment transaction committed or rolled back. Treat empty
catalog arrays as conclusive only when the recorded observer facts show sufficient
metadata visibility.

If target resolution, token acquisition, SQL authentication, evidence collection, or
cleanup fails, treat the run as incomplete. Do not infer permission state from a
partial artifact, and do not change grants manually. Resolve any retained exact rule
under an approved incident procedure before retrying.

For service principals/managed identities, compare the observed SID to the verified
**client ID**, not the directory object ID. The latter is used for identity binding
and RBAC. Resolution uses the exact App Service's scoped ARM managed-identity
metadata; it does not require Graph grants and never follows a credential URL.

A diagnosed historical object-ID SID may be corrected only through the separately
approved deployment confirmation `REPAIR-OBJECT-ID-SID`, with the preconditions in
`docs/knowledge/security.md`. This diagnostic remains SELECT-only and cannot request
repair. A passing local SQL regression or successful bootstrap is not a live API
login proof; verify the SQL-backed catalog after the authorized repair.
