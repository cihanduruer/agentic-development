$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$development = Get-Content (Join-Path $root '.github/workflows/deploy-development.yml') -Raw
$production = Get-Content (Join-Path $root '.github/workflows/deploy-production.yml') -Raw
$release = Get-Content (Join-Path $root '.github/workflows/release-proposal.yml') -Raw
$contractAssertion = Get-Content (Join-Path $root 'scripts/Assert-ProductionDeploymentContract.ps1') -Raw
$contract = Get-Content (Join-Path $root 'infra/deployment-contract.json') -Raw | ConvertFrom-Json

if ($contract.name -ne 'entra-sql-managed-identity' -or $contract.version -ne 1) {
    throw 'The production deployment contract is missing or invalid.'
}
if ($release -notmatch 'deployment-contract\.json' -or
    $release -notmatch 'deploymentContractVersion' -or
    $production -notmatch 'Assert-ProductionDeploymentContract\.ps1' -or
    $contractAssertion -notmatch 'entra-sql-managed-identity') {
    throw 'Release packaging and production preflight must enforce the deployment contract.'
}
foreach ($workflow in @($development, $production)) {
    if ($workflow -notmatch 'Remove-LegacySqlCredential\.ps1' -or
        $workflow -notmatch 'Invoke-KnowledgeIndexerWithRetry\.ps1' -or
        $workflow -notmatch 'MaximumWaitSeconds 600') {
        throw 'Azure deployment workflows must clean up legacy SQL credentials and use bounded Search RBAC retries.'
    }
}

Write-Output 'Azure data hardening workflow contracts passed.'
exit 0
