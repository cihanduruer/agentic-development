# Copilot cloud-agent Azure Boards authentication

## Scope

This runbook describes passwordless Azure DevOps authentication for Copilot cloud-agent sessions. It does not change Hotel requirement capture or install an Azure DevOps MCP server.

The `.github/workflows/copilot-setup-steps.yml` workflow follows GitHub's [Azure DevOps Copilot setup example](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/configure-mcp-servers#example-azure-devops). Its single `copilot-setup-steps` job uses the `copilot` environment, GitHub OIDC, and the dedicated `agentic-hotelbooking-copilot-boards` Entra application. Configure the same public `AZURE_BOARDS_CLIENT_ID` and `AZURE_BOARDS_TENANT_ID` values as **both repository variables** (for standalone manual setup checks) and **`copilot` environment variables** (for automatic cloud-agent setup). Repository-only variables were not resolved in automatic run 36729696517; adding the matching public values to the `copilot` environment restored login. No password, certificate, PAT, subscription, or Hotel deployment identity is used.

The federated credential is restricted to issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and subject `repo:cihanduruer@1026905/agentic-development@1394453319:environment:copilot`. Retain this immutable repository identity format because GitHub immutable subject claims are enabled. The application has Stakeholder registration in `ai-enabled-ado-org`, project-level View permission, and root-area Work Item Read/Edit permission. It has no Contributors/admin membership or Azure resource role assignments.

## Session behavior and verification

Copilot runs the setup job before it starts the agent. The `workflow_dispatch` trigger also permits a trusted standalone check; that manual run is accepted only from `main`. There is no pull-request trigger, so reviewing a change does not authenticate to Azure.

`azure/login` normally clears the Azure CLI account cache in its post step. The pinned action's `AZURE_LOGIN_POST_CLEANUP: 'false'` setting prevents that cleanup so the login remains available to the agent tool phase. The cache exists only in the ephemeral hosted session. The startup smoke gets an Azure DevOps token in a shell variable, masks it immediately, and makes only a `GET` request to the `sample-project` metadata endpoint. It checks HTTP 200 and the exact project ID without printing the token, authorization header, CLI cache, or response body, and does not upload artifacts.

For an independent setup check, dispatch **Copilot setup steps** from `main`. Missing variables, login failure, request failure, non-200 response, or an unexpected project ID fails the job explicitly. Do not use interactive/device login or print tokens, headers, CLI cache files, or environment dumps while diagnosing failures.

## Copilot cloud-agent network access

The Copilot cloud-agent firewall must remain enabled. In repository settings, open **Settings > Copilot > Internet access > Copilot cloud agent** and add these narrowly scoped HTTPS origins to the allowlist:

- `https://dev.azure.com/ai-enabled-ado-org`
- `https://login.microsoftonline.com`

The initial investigation on 2026-09-30 confirmed the firewall and recommended allowlist were enabled, while the custom allowlist was empty at that time. This is a dated snapshot, not a statement of the current configuration. Do not disable the firewall, add a proxy, or bypass its policy. The setup job runs before the agent firewall applies, so its successful login and project smoke do not establish that agent tools can reach Azure Boards. Require a real Azure Boards request from the agent tool phase, through the enabled firewall, before reporting cloud-agent access as verified.

The setup does not configure MCP. If the Azure DevOps MCP server is already configured, it can use `-a azcli`; otherwise use Entra-authenticated REST requests directly. Do not assume Azure DevOps CLI service-principal login is supported. Fresh cloud sessions obtain their own setup login; an already-running session may need to be restarted after the setup workflow is available.

## Requirement-capture boundary

This platform authentication work makes no work-item changes. For future new Hotel requirements, retain the existing capture contract: create only a User Story in `sample-project`, leave it `New`, omit `github-synced`, and let scheduled intake own later synchronization and assignment. Do not manually dispatch intake or mark the story `Active`.

## Evidence status

Passing the setup job proves only the authenticated read-only project metadata check outside the agent firewall. On 2026-09-30, standalone manual setup run 36729520532 passed; it did not prove agent-phase access. In hosted agent tool phase 709f8381-36c6-4474-a50a-4f58da197491, the Azure CLI reported the dedicated service principal `a82083c5-086a-4966-bb9d-012af7ff8742` in tenant `a01cc91b-6d33-4777-9c32-0b02b5cd3473`. The runbook's read-only verification step was executed in that phase with the firewall enabled and succeeded: HTTP 200 and project ID `7f3adf73-a1ef-43f6-93cb-bf61a166abb2`. It made no work-item writes. This establishes agent-phase Azure Boards project read access, not Playwright or other application behavior.
