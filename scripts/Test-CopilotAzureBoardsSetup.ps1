$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repositoryRoot '.github/workflows/copilot-setup-steps.yml'
$runbookPath = Join-Path $repositoryRoot 'docs/runbooks/copilot-azure-boards-authentication.md'
$instructionsPath = Join-Path $repositoryRoot '.github/copilot-instructions.md'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
$runbook = Get-Content -LiteralPath $runbookPath -Raw
$instructions = Get-Content -LiteralPath $instructionsPath -Raw

function Assert-Contains {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Failure
    )

    if ($Content -notmatch $Pattern) {
        throw $Failure
    }
}

function Assert-NotContains {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Failure
    )

    if ($Content -match $Pattern) {
        throw $Failure
    }
}

Assert-Contains $workflow '(?m)^on:\s*\r?\n\s{2}workflow_dispatch:\s*$' 'Setup workflow must allow manual dispatch.'
Assert-NotContains $workflow '(?m)^\s{2}(pull_request|pull_request_target|push|schedule):' 'Setup workflow must not authenticate on pull requests, pushes, or schedules.'
Assert-Contains $workflow '(?m)^  copilot-setup-steps:\s*$' 'Setup workflow must define the required Copilot setup job.'
Assert-Contains $workflow '(?m)^\s{4}environment: copilot\s*$' 'Setup job must use the copilot environment.'
Assert-Contains $workflow '(?m)^\s{6}id-token: write\s*$' 'Setup job must grant OIDC token permission.'
Assert-Contains $workflow '(?m)^\s{6}contents: read\s*$' 'Setup job must limit repository permission to contents: read.'
Assert-Contains $workflow '(?m)^\s{8}if: github\.event_name == ''workflow_dispatch'' && github\.ref != ''refs/heads/main''' 'Standalone manual authentication must be guarded to main.'
Assert-Contains $workflow 'uses: azure/login@a457da9ea143d694b1b9c7c869ebb04ebe844ef5' 'Azure Login must use the approved immutable pin.'
Assert-Contains $workflow 'AZURE_LOGIN_POST_CLEANUP: ''false''' 'Azure Login cleanup must preserve the session CLI login.'
Assert-Contains $workflow 'allow-no-subscriptions: true' 'Azure Login must support the subscription-free Boards tenant.'
Assert-Contains $workflow 'vars\.AZURE_BOARDS_CLIENT_ID' 'Login must use the public Azure Boards client ID variable.'
Assert-Contains $workflow 'vars\.AZURE_BOARDS_TENANT_ID' 'Login must use the public Azure Boards tenant ID variable.'
Assert-NotContains $workflow 'secrets\.AZURE_BOARDS_' 'Azure Boards public identifiers must not be configured as secrets.'
Assert-Contains $workflow 'Missing repository variable AZURE_BOARDS_CLIENT_ID' 'Missing client ID must fail explicitly.'
Assert-Contains $workflow 'Missing repository variable AZURE_BOARDS_TENANT_ID' 'Missing tenant ID must fail explicitly.'

$smokeStart = $workflow.IndexOf('      - name: Verify read-only Azure Boards access')
if ($smokeStart -lt 0) {
    throw 'Setup workflow must include the read-only Azure Boards smoke check.'
}
$smoke = $workflow.Substring($smokeStart)
Assert-Contains $smoke 'az account get-access-token' 'Smoke check must acquire an Azure DevOps token through Azure CLI.'
Assert-Contains $smoke 'Could not obtain an Azure DevOps access token' 'Smoke check must report Azure CLI authentication failure explicitly.'
Assert-Contains $smoke '--resource 499b84ac-1321-427f-aa17-267ca6975798' 'Smoke check must request the Azure DevOps resource token.'
Assert-Contains $smoke 'echo "::add-mask::\$token"' 'Smoke check must immediately mask the in-memory token.'
Assert-Contains $smoke 'curl --silent --show-error' 'Smoke check must make a quiet direct REST request.'
Assert-Contains $smoke 'auth_header_name=''Authorization:''[\s\S]*auth_scheme=''Bearer''[\s\S]*--header "\$auth_header_name \$auth_scheme \$token"' 'Smoke check must send the masked token as an authorization header.'
Assert-Contains $smoke '--request GET' 'Smoke check must use HTTP GET.'
Assert-Contains $smoke "'https://dev\.azure\.com/ai-enabled-ado-org/_apis/projects/sample-project\?api-version=7\.1'" 'Smoke check must read only sample-project metadata.'
Assert-Contains $smoke '\.id == \$expected' 'Smoke check must validate the exact project ID field.'
Assert-Contains $smoke '7f3adf73-a1ef-43f6-93cb-bf61a166abb2' 'Smoke check must validate the provisioned sample-project ID.'
Assert-Contains $smoke 'unset token' 'Smoke check must clear the in-memory token after the request.'
if ($smoke.IndexOf('echo "::add-mask::$token"') -gt $smoke.IndexOf('curl --silent --show-error')) {
    throw 'Smoke check must mask the token before making the request.'
}
Assert-NotContains $smoke '(?im)^\s*(echo|printf)\s+["'']?\$token' 'Smoke check must not print the token.'
Assert-NotContains $smoke '(?im)\bcurl\b[^\r\n]*(-v|--verbose)' 'Smoke check must not enable verbose HTTP logging.'
Assert-NotContains $smoke '(?im)\b(az\s+(boards|resource|group|account\s+set)|az\s+devops\s+)' 'Setup must not make Azure resource or Boards write calls.'
Assert-NotContains $workflow '(?im)upload-artifact|actions/upload-artifact|printenv|^\s*env\s*$' 'Setup must not upload credentials or dump the environment.'

Assert-Contains $runbook 'Stakeholder registration' 'Runbook must document the least-privilege Azure DevOps access.'
Assert-Contains $runbook 'AZURE_LOGIN_POST_CLEANUP' 'Runbook must explain why CLI cleanup is disabled.'
Assert-Contains $runbook 'Fresh cloud sessions obtain their own setup login' 'Runbook must distinguish fresh and existing sessions.'
Assert-Contains $runbook 'create only a User Story in `sample-project`, leave it `New`, omit `github-synced`' 'Runbook must preserve the existing requirement-capture contract.'
Assert-Contains $runbook 'Do not claim sign-in is fixed until a real hosted Copilot cloud session' 'Runbook must not overstate verification.'
Assert-Contains $instructions 'copilot-azure-boards-authentication\.md' 'Copilot instructions must point to the authentication runbook.'

Write-Output 'Copilot Azure Boards setup contract passed.'
