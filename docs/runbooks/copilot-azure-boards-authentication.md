# Copilot cloud-agent Azure Boards authentication

## Scope

This runbook describes passwordless Azure DevOps authentication for Copilot cloud-agent sessions. It does not change Hotel requirement capture or install an Azure DevOps MCP server.

The `.github/workflows/copilot-setup-steps.yml` workflow follows GitHub's [Azure DevOps Copilot setup example](https://docs.github.com/en/copilot/how-tos/copilot-on-github/customize-copilot/configure-mcp-servers#example-azure-devops). Its single `copilot-setup-steps` job uses the `copilot` environment, GitHub OIDC, and the dedicated `agentic-hotelbooking-copilot-boards` Entra application. The only Azure identity inputs are the public repository variables `AZURE_BOARDS_CLIENT_ID` and `AZURE_BOARDS_TENANT_ID`; no password, certificate, PAT, subscription, or Hotel deployment identity is used.

The federated credential is restricted to issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and subject `repo:cihanduruer/agentic-development:environment:copilot`. The application has Stakeholder registration in `ai-enabled-ado-org`, project-level View permission, and root-area Work Item Read/Edit permission. It has no Contributors/admin membership or Azure resource role assignments.

## Session behavior and verification

Copilot runs the setup job before it starts the agent. The `workflow_dispatch` trigger also permits a trusted standalone check; that manual run is accepted only from `main`. There is no pull-request trigger, so reviewing a change does not authenticate to Azure.

`azure/login` normally clears the Azure CLI account cache in its post step. The pinned action's `AZURE_LOGIN_POST_CLEANUP: 'false'` setting prevents that cleanup so the login remains available to the agent tool phase. The cache exists only in the ephemeral hosted session. The startup smoke gets an Azure DevOps token in a shell variable, masks it immediately, and makes only a `GET` request to the `sample-project` metadata endpoint. It checks HTTP 200 and the exact project ID without printing the token, authorization header, CLI cache, or response body, and does not upload artifacts.

For an independent setup check, dispatch **Copilot setup steps** from `main`. Missing variables, login failure, request failure, non-200 response, or an unexpected project ID fails the job explicitly. Do not use interactive/device login or print tokens, headers, CLI cache files, or environment dumps while diagnosing failures.

The setup does not configure MCP. If the Azure DevOps MCP server is already configured, it can use `-a azcli`; otherwise use Entra-authenticated REST requests directly. Do not assume Azure DevOps CLI service-principal login is supported. Fresh cloud sessions obtain their own setup login; an already-running session may need to be restarted after the setup workflow is available.

## Requirement-capture boundary

This platform authentication work makes no work-item changes. For future new Hotel requirements, retain the existing capture contract: create only a User Story in `sample-project`, leave it `New`, omit `github-synced`, and let scheduled intake own later synchronization and assignment. Do not manually dispatch intake or mark the story `Active`.

## Evidence status

Passing the setup job proves only the authenticated read-only project metadata check. Do not claim sign-in is fixed until a real hosted Copilot cloud session confirms Azure Boards access. Keep issue #43 open until that live session evidence is reviewed.
