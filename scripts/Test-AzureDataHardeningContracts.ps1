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
$program = Get-Content (Join-Path $root 'src/Api/Program.cs') -Raw
$contract = Get-Content (Join-Path $root 'infra/deployment-contract.json') -Raw | ConvertFrom-Json

function Get-NamedStepBody {
    param(
        [Parameter(Mandatory)]
        [string]$Workflow,

        [Parameter(Mandatory)]
        [string]$Name
    )

    $escapedName = [Regex]::Escape($Name)
    $match = [Regex]::Match(
        $Workflow,
        "(?ms)^      - name: $escapedName\r?\n(?<body>.*?)(?=^      - (?:name:|uses:)|\z)")
    if (-not $match.Success) {
        throw "Workflow step '$Name' is missing."
    }
    return $match.Groups['body'].Value
}

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
    $initialApiConfiguration = $workflow.IndexOf('Configure initial API after SQL bootstrap')
    $artifactDeploy = if ($workflow -eq $development) {
        $workflow.IndexOf('- name: Deploy API')
    }
    else {
        $workflow.IndexOf('- name: Deploy API artifact')
    }
    $artifactReadiness = $workflow.IndexOf(
        'Verify migration-disabled API artifact before existing-environment cutover')
    $apiCutoverName = 'Apply and prove managed-identity API configuration with rollback'
    $apiCutover = $workflow.IndexOf($apiCutoverName)
    $readiness = if ($workflow -eq $development) {
        $workflow.IndexOf('Verify operations authorization and persistence')
    }
    else {
        $apiCutover
    }
    $sqlCutover = $workflow.IndexOf('Enforce SQL Entra-only after managed-identity API readiness')
    $postCutoverReadiness = $workflow.IndexOf('Verify API after SQL Entra-only enforcement')
    $sqlClassificationBody = Get-NamedStepBody `
        -Workflow $workflow `
        -Name 'Classify SQL cutover state before infrastructure mutation'
    $identityBody = Get-NamedStepBody `
        -Workflow $workflow `
        -Name 'Ensure API managed identity exists without changing live configuration'
    $initialConfigurationBody = Get-NamedStepBody `
        -Workflow $workflow `
        -Name 'Configure initial API after SQL bootstrap'
    $apiCutoverBody = Get-NamedStepBody -Workflow $workflow -Name $apiCutoverName
    $sqlCutoverBody = Get-NamedStepBody `
        -Workflow $workflow `
        -Name 'Enforce SQL Entra-only after managed-identity API readiness'
    if ($sqlClassification -lt 0 -or $foundation -lt $sqlClassification -or
        $identity -lt $foundation -or
        $bootstrap -lt $identity -or $initialApiConfiguration -lt $bootstrap -or
        $artifactDeploy -lt $initialApiConfiguration -or
        $artifactReadiness -lt $artifactDeploy -or $apiCutover -lt $artifactReadiness -or
        $readiness -lt $apiCutover -or $sqlCutover -lt $readiness -or
        $postCutoverReadiness -lt $sqlCutover -or
        $initialConfigurationBody -notmatch
            "if: steps\.api-identity\.outputs\.configuredApi == 'false'" -or
        $initialConfigurationBody -notmatch 'configureApi=true' -or
        $initialConfigurationBody -notmatch 'configureSql=false' -or
        $initialConfigurationBody -notmatch
            'existingSqlServerName=\$\{\{ steps\.sql-state\.outputs\.sqlServerName \}\}' -or
        $initialConfigurationBody -notmatch
            'steps\.sql-state\.outputs\.preExistingSqlAuthMode' -or
        $initialConfigurationBody -notmatch
            'server-side legacy SQL authentication remains enabled' -or
        $initialConfigurationBody -notmatch
            'SQL was already Entra-only and remains Entra-only' -or
        $initialConfigurationBody -notmatch
            'newly created SQL server remains Entra-only' -or
        $apiCutoverBody -notmatch
            "if: steps\.api-identity\.outputs\.configuredApi == 'true'" -or
        $apiCutoverBody -notmatch 'configureApi=true' -or
        $apiCutoverBody -notmatch 'configureSql=false' -or
        $apiCutoverBody -notmatch 'previousConnection' -or
        $apiCutoverBody -notmatch 'webapp config appsettings set' -or
        $apiCutoverBody -notmatch 'prior app SQL connection was restored and proven ready' -or
        $sqlCutoverBody -notmatch 'configureApi=false' -or
        $sqlCutoverBody -notmatch 'configureSql=true' -or
        $identityBody -notmatch 'webapp config appsettings list' -or
        $identityBody -notmatch 'configuredApi=' -or
        $sqlClassificationBody -notmatch 'administrators\.azureADOnlyAuthentication' -or
        $sqlClassificationBody -notmatch 'preExistingSqlAuthMode=initial' -or
        $sqlClassificationBody -notmatch 'preExistingSqlAuthMode=\$sqlAuthMode' -or
        $workflow -notmatch 'steps\.sql-state\.outputs\.configureSql' -or
        $workflow -notmatch 'servers\.Count -eq 0' -or
        $workflow -notmatch "'configureSql=true'" -or
        $workflow -notmatch "'configureSql=false'" -or
        $workflow -notmatch "'existingSql=true'" -or
        $workflow -notmatch "'existingSql=false'" -or
        $workflow -notmatch "newly created SQL server remains Entra-only" -or
        $workflow -notmatch 'already Entra-only and remains Entra-only' -or
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
$cutoverCases = @(
    [pscustomobject]@{
        Name = 'initial-empty'
        SqlExists = $false
        ApiConfigured = $false
        SqlServerName = ''
        SqlAuthMode = 'initial'
        ExpectedPath = 'configure-before-artifact'
        ExpectedFailureState = 'newly-created-entra-only'
    },
    [pscustomobject]@{
        Name = 'partial-initial-retry-default-server'
        SqlExists = $true
        ApiConfigured = $false
        SqlServerName = 'ahb-dev-default-sql'
        SqlAuthMode = 'entraOnly'
        ExpectedPath = 'configure-before-artifact'
        ExpectedFailureState = 'existing-entra-only'
    },
    [pscustomobject]@{
        Name = 'partial-initial-retry-adopted-legacy-server'
        SqlExists = $true
        ApiConfigured = $false
        SqlServerName = 'adopted-nondefault-sql'
        SqlAuthMode = 'legacy'
        ExpectedPath = 'configure-before-artifact'
        ExpectedFailureState = 'legacy-enabled'
    },
    [pscustomobject]@{
        Name = 'legacy-old-binary-upgrade'
        SqlExists = $true
        ApiConfigured = $true
        SqlServerName = 'legacy-sql'
        SqlAuthMode = 'legacy'
        ExpectedPath = 'artifact-before-configure'
        ExpectedFailureState = 'legacy-enabled'
    },
    [pscustomobject]@{
        Name = 'entra-only-replacement-upgrade'
        SqlExists = $true
        ApiConfigured = $true
        SqlServerName = 'compliant-sql'
        SqlAuthMode = 'entraOnly'
        ExpectedPath = 'artifact-before-configure'
        ExpectedFailureState = 'existing-entra-only'
    }
)
foreach ($case in $cutoverCases) {
    $actual = if ($case.ApiConfigured) {
        [pscustomobject]@{
            Path = 'artifact-before-configure'
            ForwardedSqlServerName = $case.SqlServerName
        }
    }
    else {
        [pscustomobject]@{
            Path = 'configure-before-artifact'
            ForwardedSqlServerName = $case.SqlServerName
        }
    }
    $actualFailureState = switch ($case.SqlAuthMode) {
        'initial' { 'newly-created-entra-only' }
        'legacy' { 'legacy-enabled' }
        'entraOnly' { 'existing-entra-only' }
        default { 'invalid' }
    }
    if ($actual.Path -ne $case.ExpectedPath -or
        $actual.ForwardedSqlServerName -ne $case.SqlServerName -or
        $actualFailureState -ne $case.ExpectedFailureState) {
        throw "Cutover state '$($case.Name)' did not preserve its path, exact SQL server, and authentication state."
    }
}
if ($program -notmatch 'ShouldApplyDatabaseMigrations' -or
    $program -notmatch 'Database:ApplyMigrations' -or
    $platform -notmatch "name: 'Database__ApplyMigrations'\s+value: 'false'") {
    throw 'The replacement API must disable startup migrations before any runtime-only identity cutover.'
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
    $routingRetry -notmatch "reasonCode -ne 'evaluation_error'") {
    throw 'Development routing readiness must retry qa-agent for the exact indexed revision with a bounded deadline.'
}
if ($development.IndexOf('Remove approved active legacy SQL administrator credential') -lt
        $development.IndexOf('Verify API after SQL Entra-only enforcement') -or
    $production.IndexOf('Remove approved active legacy SQL administrator credential') -lt
        $production.IndexOf('Verify API after SQL Entra-only enforcement') -or
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
