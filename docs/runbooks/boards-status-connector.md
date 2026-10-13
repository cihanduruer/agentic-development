# Azure Boards delivery-status connector provisioning

**Scope:** human-only credential and entitlement setup for the read-only connector used by
`GET /api/delivery/status`. This runbook does not create Boards items or modify work-item state.
The configured organization and project are fixed to `ai-enabled-ado-org/sample-project`.

## Security boundary

- The API calls Azure DevOps REST API v7.1 only from the backend. The browser never receives or
  presents an Azure DevOps credential.
- `GET /api/delivery/status` has no sign-in or authorization. CORS and the fact that the page is
  under `/admin` do not restrict direct API access; anyone who can reach the API can request a
  recent delivery's fixed Boards projection. Before connecting Boards, confirm that the selected
  work items' title, state, type, tags, changed date, canonical URL, and typed links are approved
  for public disclosure. Do not use this connector for internal-only or otherwise sensitive items.
- The preferred mode is the API App Service system-assigned managed identity, using resource
  `499b84ac-1321-427f-aa17-267ca6975798`.
- The API uses only GET for a selected positive integer ID found among recent GitHub deliveries.
- Grant the identity Azure DevOps **Basic** organization access and **Reader** in `sample-project`.
  Do not grant Contributor, Project Administrator, organization-owner, or write access.
- Provisioning is deliberately not automated by Bicep. Azure DevOps organization membership is a
  separate control plane from Azure RBAC.
- The connector is **unverified** until an authorized human completes the steps below and observes
  a successful live API projection. Source configuration, fake HTTP tests, and a healthy App Service
  do not prove this connection.

## Add the App Service identity

1. Deploy the API with the `ManagedIdentity` mode. In Azure Portal, open the development API App
   Service, then **Identity → System assigned**. Confirm **Status: On**. Record the displayed
   **Object (principal) ID** and the App Service resource name; do not copy credentials or tokens.
2. In Microsoft Entra admin center, open **Identity → Applications → Enterprise applications**.
   Find the service principal whose object ID exactly matches the App Service's system-assigned
   principal ID. Confirm the identity is the API identity, not the GitHub Actions deployment
   principal or another Hotel service.
3. Sign in to the Azure DevOps organization
   `https://dev.azure.com/ai-enabled-ado-org` with an organization owner.
4. Open **Organization settings → Users → Add users**. Search for/select the App Service managed
   identity service principal by its exact enterprise-application identity. If the picker does not
   resolve the exact principal, stop and ask the Entra/Azure DevOps administrator to make the
   service principal discoverable; do not substitute a user or broader identity.
5. Set **Access level** to **Basic**, select only the `sample-project` project, and add the identity.
   Confirm the resulting organization user entry is shown as a service principal with Basic access.
6. In `sample-project`, open **Project settings → Permissions → Readers → Members → Add**. Add the
   same service principal. Confirm its effective membership is Reader and that it is not a member
   of Contributors, Project Administrators, or another write-capable group.
7. Record nonsecret evidence for the deployment change: App Service resource name, principal object
   ID, organization/project, Basic access, Reader membership, operator, and UTC verification time.
   Do not put tokens or secrets in this record.

## Optional protected PAT fallback

Managed identity is preferred. Use a PAT only when the authorized operator has explicitly chosen
the `Pat` fallback:

1. Create a short-lived Azure DevOps PAT for the same organization with only Work Items read scope.
2. In the API App Service, use **Settings → Environment variables** (or the approved Key Vault
   reference workflow) to set `BoardsStatus__AuthMode=Pat` and `BoardsStatus__Pat=<PAT>`.
   Mark the setting protected/slot-sticky as supported by the hosting configuration. Never enter
   the value in appsettings, Bicep parameters, source, terminal output, tickets, logs, or chat.
3. Restart the API only through the approved deployment/operator process and verify the startup
   diagnostic reports the mode name only. Rotate/remove the PAT through the protected setting when
   returning to managed identity.

The optional GitHub token follows the same protected-setting rule (`GitHubStatus__Token`) and is
not required for public repository metadata. Use a read-only token only if public API rate limits
require it.

## Verify without exposing credentials

1. Confirm App Service configuration contains the allowlisted organization and project and the
   intended auth mode. Bicep sets only those nonsecret values; it must never receive a secret.
2. Select only an AB# item approved for public disclosure and present in recent repository GitHub
   issues; load `/api/delivery/status?workItemId=<id>` through the Hotel UI/API. A private
   verification context does not make the anonymous endpoint private.
3. Verify the response contains only the projected title, state, type, tags, changed date, canonical
   work-item URL, and typed allowlisted links. Confirm the selected item's Boards connection changes
   from unverified to connected after a successful response.
4. Check that unauthorized access, a missing item, a timeout, malformed upstream data, and rate
   limiting show an explicit not-connected/stale state without raw upstream content. Never use a
   production work item or mutate a live item as a test.
5. Retain the sanitized verification record. Only then may deployment evidence mark this external
   provisioning step verified.

No live verification was performed as part of repository implementation. Until the manual steps and
evidence above are completed, report the Azure Boards connection as **unverified**.
