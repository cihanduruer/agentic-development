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

function Assert-ReadOnlyCurlRequest {
    param([string] $Smoke)

    $normalizedSmoke = [regex]::Replace($Smoke, '\\\r?\n[ \t]*', ' ')
    $curlPattern = '(?i)(?<![\w.-])curl(?=\s)'
    $curlMatches = [regex]::Matches($normalizedSmoke, $curlPattern)
    if ($curlMatches.Count -ne 1) {
        throw 'Smoke check must contain exactly one curl invocation.'
    }

    $curlLines = @($normalizedSmoke -split '\r?\n' | Where-Object { [regex]::IsMatch($_, $curlPattern) })
    if ($curlLines.Count -ne 1) {
        throw 'Smoke check must contain exactly one curl command line.'
    }

    $curlLine = $curlLines[0]
    $methods = [regex]::Matches($curlLine, '(?<!\S)(?:--request(?:=|\s+)|-X(?:\s+)?)([^\s]+)')
    if ($methods.Count -ne 1 -or $methods[0].Groups[1].Value -cne 'GET') {
        throw 'Smoke check must explicitly use GET only, without method overrides.'
    }

    if ([regex]::IsMatch($curlLine, '(?<!\S)(?:--location(?:-trusted)?|-[A-Za-z]*L[A-Za-z]*)(?=\s|=|$)')) {
        throw 'Smoke check must not follow redirects.'
    }

    if ([regex]::IsMatch($curlLine, '(?<!\S)(?:--verbose|-[A-Za-z]*v[A-Za-z]*)(?=\s|=|$)')) {
        throw 'Smoke check must not enable verbose HTTP logging.'
    }

    if ([regex]::IsMatch($curlLine, '(?<!\S)(?:--(?:data(?:-[\w-]+)?|form(?:-string|-escape)?|upload-file|json)|-[A-Za-z]*[dFT])')) {
        throw 'Smoke check must not send request bodies or upload files.'
    }
}

function Assert-CurlMutationRejected {
    param(
        [string] $Smoke,
        [string] $Mutation
    )

    try {
        Assert-ReadOnlyCurlRequest $Smoke
    }
    catch {
        return
    }

    throw "Curl contract accepted the $Mutation mutation."
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
Assert-ReadOnlyCurlRequest $smoke
Assert-Contains $smoke '--connect-timeout 15' 'Smoke check must bound connection establishment.'
Assert-Contains $smoke '--max-time 60' 'Smoke check must bound total request duration.'
Assert-Contains $smoke "'https://dev\.azure\.com/ai-enabled-ado-org/_apis/projects/sample-project\?api-version=7\.1'" 'Smoke check must read only sample-project metadata.'
Assert-Contains $smoke '\.id == \$expected' 'Smoke check must validate the exact project ID field.'
Assert-Contains $smoke '7f3adf73-a1ef-43f6-93cb-bf61a166abb2' 'Smoke check must validate the provisioned sample-project ID.'
Assert-Contains $smoke 'unset token' 'Smoke check must clear the in-memory token after the request.'
if ($smoke.IndexOf('echo "::add-mask::$token"') -gt $smoke.IndexOf('curl --silent --show-error')) {
    throw 'Smoke check must mask the token before making the request.'
}
Assert-NotContains $smoke '(?im)^\s*(echo|printf)\s+["'']?\$token' 'Smoke check must not print the token.'
Assert-NotContains $smoke '(?im)\b(az\s+(boards|resource|group|account\s+set)|az\s+devops\s+)' 'Setup must not make Azure resource or Boards write calls.'
Assert-NotContains $workflow '(?im)upload-artifact|actions/upload-artifact|printenv|^\s*env\s*$' 'Setup must not upload credentials or dump the environment.'

$continuation = '\' + [Environment]::NewLine + '            '
$mutations = @(
    [pscustomobject]@{ Name = 'a second PATCH request'; Smoke = $smoke + [Environment]::NewLine + 'curl --request PATCH https://example.invalid' }
    [pscustomobject]@{ Name = 'an overridden method'; Smoke = $smoke.Replace('--request GET', '--request PATCH --request GET') }
    [pscustomobject]@{ Name = 'a data option'; Smoke = $smoke.Replace('--request GET', '--request GET --data payload') }
    [pscustomobject]@{ Name = 'an upload option'; Smoke = $smoke.Replace('--request GET', '--request GET --upload-file payload') }
    [pscustomobject]@{ Name = 'attached -d data'; Smoke = $smoke.Replace('--request GET', '--request GET -d@payload') }
    [pscustomobject]@{ Name = 'attached -T upload'; Smoke = $smoke.Replace('--request GET', '--request GET -T/tmp/payload') }
    [pscustomobject]@{ Name = 'a multiline -L redirect option'; Smoke = $smoke.Replace('--request GET', "--request GET$continuation-L") }
    [pscustomobject]@{ Name = 'a multiline --location redirect option'; Smoke = $smoke.Replace('--request GET', "--request GET$continuation--location") }
    [pscustomobject]@{ Name = 'a multiline -v verbose option'; Smoke = $smoke.Replace('--request GET', "--request GET$continuation-v") }
    [pscustomobject]@{ Name = 'a multiline --verbose option'; Smoke = $smoke.Replace('--request GET', "--request GET$continuation--verbose") }
)
foreach ($mutation in $mutations) {
    Assert-CurlMutationRejected $mutation.Smoke $mutation.Name
}

Assert-Contains $runbook 'Stakeholder registration' 'Runbook must document the least-privilege Azure DevOps access.'
Assert-Contains $runbook 'subject `repo:cihanduruer@1026905/agentic-development@1394453319:environment:copilot`' 'Runbook must document the confirmed immutable GitHub OIDC subject.'
Assert-Contains $runbook 'immutable repository identity format because GitHub immutable subject claims are enabled' 'Runbook must explain why the immutable repository identity format is retained.'
Assert-Contains $runbook 'AZURE_LOGIN_POST_CLEANUP' 'Runbook must explain why CLI cleanup is disabled.'
Assert-Contains $runbook 'Fresh cloud sessions obtain their own setup login' 'Runbook must distinguish fresh and existing sessions.'
Assert-Contains $runbook 'Settings > Copilot > Internet access > Copilot cloud agent' 'Runbook must give the confirmed firewall settings location.'
Assert-Contains $runbook 'https://dev\.azure\.com/ai-enabled-ado-org' 'Runbook must document the Azure DevOps allowlist origin.'
Assert-Contains $runbook 'https://login\.microsoftonline\.com' 'Runbook must document the Entra login allowlist origin.'
Assert-Contains $runbook 'firewall must remain enabled' 'Runbook must require the cloud-agent firewall to remain enabled.'
Assert-Contains $runbook 'setup job runs before the agent firewall applies' 'Runbook must distinguish setup networking from agent-tool networking.'
Assert-Contains $runbook 'real Azure Boards request from the agent tool phase' 'Runbook must require tool-phase access evidence.'
Assert-Contains $runbook 'create only a User Story in `sample-project`, leave it `New`, omit `github-synced`' 'Runbook must preserve the existing requirement-capture contract.'
Assert-Contains $runbook 'agent-tool-phase Azure Boards request through the enabled firewall' 'Runbook must not overstate verification.'
Assert-Contains $instructions 'copilot-azure-boards-authentication\.md' 'Copilot instructions must point to the authentication runbook.'

Write-Output 'Copilot Azure Boards setup contract passed.'
