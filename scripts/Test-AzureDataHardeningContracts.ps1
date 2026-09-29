$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$development = Get-Content (Join-Path $root '.github/workflows/deploy-development.yml') -Raw
$production = Get-Content (Join-Path $root '.github/workflows/deploy-production.yml') -Raw
$release = Get-Content (Join-Path $root '.github/workflows/release-proposal.yml') -Raw
$contractAssertion = Get-Content (Join-Path $root 'scripts/Assert-ProductionDeploymentContract.ps1') -Raw
$cleanup = Get-Content (Join-Path $root 'scripts/Remove-LegacySqlCredential.ps1') -Raw
$indexRetry = Get-Content (Join-Path $root 'scripts/Invoke-KnowledgeIndexerWithRetry.ps1') -Raw
$routingRetry = Get-Content (Join-Path $root 'scripts/Invoke-RoutingReadinessWithRetry.ps1') -Raw
$platform = Get-Content (Join-Path $root 'infra/modules/platform.bicep') -Raw
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
foreach ($workflow in @($development, $production)) {
    $sqlClassification = $workflow.IndexOf('Classify SQL cutover state before infrastructure mutation')
    $foundation = $workflow.IndexOf('configureApi=false')
    $identity = $workflow.IndexOf('Ensure API managed identity exists without changing live configuration')
    $bootstrap = $workflow.IndexOf('Bootstrap API managed identity database role')
    $cutover = $workflow.IndexOf('Apply managed-identity API configuration after SQL bootstrap')
    if ($sqlClassification -lt 0 -or $foundation -lt $sqlClassification -or
        $identity -lt $foundation -or
        $bootstrap -lt $identity -or $cutover -lt $bootstrap -or
        $workflow.IndexOf('configureApi=true', $cutover) -lt $cutover -or
        $workflow.IndexOf('configureSql=true', $cutover) -lt $cutover -or
        $workflow -notmatch 'steps\.sql-state\.outputs\.configureSql' -or
        $workflow -notmatch 'servers\.Count -eq 0' -or
        $workflow -notmatch "'configureSql=true'" -or
        $workflow -notmatch "'configureSql=false'" -or
        $workflow -notmatch 'sqlServerName=\$\(\$servers\[0\]\.name\)' -or
        $workflow -notmatch 'multiple SQL servers; cutover is blocked before mutation' -or
        $workflow -notmatch 'requires the GitHub deployment principal as Entra administrator before any deployment mutation' -or
        $workflow -notmatch 'appLookupExitCode' -or
        $workflow -notmatch 'ResourceNotFound' -or
        $workflow -notmatch 'webapp identity assign' -or
        $workflow -notmatch 'infra/api-identity\.bicep') {
        throw 'API managed-identity configuration must be applied only after identity creation and SQL bootstrap.'
    }
}
if (-not $platform.Contains(
        "resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = if (configureSql) {") -or
    -not $platform.Contains(
        "resource database 'Microsoft.Sql/servers/databases@2023-08-01' = if (configureSql) {") -or
    -not $platform.Contains('value: ''Server=tcp:${sqlServerName}')) {
    throw 'Bicep must omit existing SQL from the pre-bootstrap phase and adopt its exact name at cutover.'
}
if ($development -notmatch 'Test-DevelopmentOperations\.ps1' -or
    $routingRetry -notmatch 'MaximumWaitSeconds = 600' -or
    $routingRetry -notmatch 'exact knowledge revision' -or
    $routingRetry -notmatch "effectiveWorker -eq 'qa-agent'" -or
    $routingRetry -notmatch "reason -notmatch.*evaluation_error") {
    throw 'Development routing readiness must retry qa-agent for the exact indexed revision with a bounded deadline.'
}
if ($development.IndexOf('Remove approved active legacy SQL administrator credential') -lt
        $development.IndexOf('Verify operations authorization and persistence') -or
    $production.IndexOf('Remove approved active legacy SQL administrator credential') -lt
        $production.IndexOf('Verify API runtime readiness') -or
    $development -notmatch 'DELETE-ACTIVE-LEGACY-SQL-SECRET' -or
    $production -notmatch 'DELETE-ACTIVE-LEGACY-SQL-SECRET' -or
    $cleanup -notmatch 'Explicit human approval' -or
    $cleanup -notmatch 'Runtime readiness' -or
    $cleanup -notmatch 'keyvault secret delete' -or
    $cleanup -notmatch 'approved operator' -or
    $cleanup -match 'Key Vault Secrets Officer' -or
    $cleanup -match 'role assignment create' -or
    $cleanup -match 'az resource delete') {
    throw 'Legacy credential cleanup must require approval and run only after runtime readiness.'
}
if ($indexRetry -notmatch 'AttemptTimeoutSeconds' -or
    $indexRetry -notmatch 'Kill\(\$true\)' -or
    $indexRetry -notmatch 'remainingSeconds') {
    throw 'Search indexing must terminate child processes within the overall retry deadline.'
}
if ($contractAssertion -notmatch 'is \[int\]' -or
    $contractAssertion -notmatch 'isnot \[string\]' -or
    $contractAssertion -notmatch 'JSON object root') {
    throw 'Production deployment contracts must enforce strict JSON value types.'
}

Write-Output 'Azure data hardening workflow contracts passed.'
exit 0
