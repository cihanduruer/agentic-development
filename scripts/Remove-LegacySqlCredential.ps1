param(
    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [string] $SqlServerName,

    [Parameter(Mandatory)]
    [string] $ApiAppName,

    [Parameter(Mandatory)]
    [string] $LegacyVaultName,

    [Parameter(Mandatory)]
    [Guid] $DeploymentPrincipalObjectId,

    [switch] $Approved,

    [switch] $RuntimeReadinessVerified,

    [ValidateRange(1, 40)]
    [int] $AuthorizationRetryCount = 21,

    [ValidateRange(1, 60)]
    [int] $RetryDelaySeconds = 15
)

$ErrorActionPreference = 'Stop'
if (-not $Approved) {
    throw 'Explicit human approval is required to delete the active legacy SQL credential.'
}
if (-not $RuntimeReadinessVerified) {
    throw 'Runtime readiness must be verified after managed-identity SQL bootstrap and API deployment.'
}

function Invoke-AzTsv {
    param([string[]] $Arguments)

    $output = & az @Arguments --output tsv --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }

    return ([string]$output).Trim()
}

$entraOnly = Invoke-AzTsv @(
    'sql', 'server', 'ad-only-auth', 'get',
    '--resource-group', $ResourceGroup,
    '--name', $SqlServerName,
    '--query', 'azureAdOnlyAuthentication'
)
if ($entraOnly -ne 'true') {
    throw "SQL server '$SqlServerName' is not Entra-only; legacy credential cleanup is blocked."
}

$connectionString = Invoke-AzTsv @(
    'webapp', 'config', 'appsettings', 'list',
    '--resource-group', $ResourceGroup,
    '--name', $ApiAppName,
    '--query', "[?name=='ConnectionStrings__HotelBooking'].value | [0]"
)
if ([string]::IsNullOrWhiteSpace($connectionString) -or
    $connectionString -notmatch 'Authentication=Active Directory Default' -or
    $connectionString -match '(?i)Password=|User ID=|@Microsoft\.KeyVault') {
    throw "App Service '$ApiAppName' does not have the expected passwordless SQL connection."
}

$vaultId = Invoke-AzTsv @(
    'keyvault', 'list',
    '--resource-group', $ResourceGroup,
    '--query', "[?name=='$LegacyVaultName'].id | [0]"
)
if ([string]::IsNullOrWhiteSpace($vaultId)) {
    Write-Output "Legacy Key Vault '$LegacyVaultName' is not deployed."
    exit 0
}

$secretScope = "$vaultId/secrets/sql-connection-string"
$assignmentCount = Invoke-AzTsv @(
    'role', 'assignment', 'list',
    '--assignee-object-id', $DeploymentPrincipalObjectId.ToString(),
    '--role', 'Key Vault Secrets Officer',
    '--scope', $secretScope,
    '--query', 'length(@)'
)
$createdRoleAssignmentId = ''
if ($assignmentCount -eq '0') {
    $createdRoleAssignmentId = Invoke-AzTsv @(
        'role', 'assignment', 'create',
        '--assignee-object-id', $DeploymentPrincipalObjectId.ToString(),
        '--assignee-principal-type', 'ServicePrincipal',
        '--role', 'Key Vault Secrets Officer',
        '--scope', $secretScope,
        '--query', 'id'
    )
    if ([string]::IsNullOrWhiteSpace($createdRoleAssignmentId)) {
        throw 'Azure did not return the temporary exact-secret cleanup role assignment ID.'
    }
}
elseif ($assignmentCount -ne '1') {
    throw 'Unable to establish the exact-secret cleanup role assignment.'
}

function Get-LegacySecretState {
    $result = & az keyvault secret list-versions `
        --vault-name $LegacyVaultName `
        --name sql-connection-string `
        --maxresults 1 `
        --query 'length(@)' `
        --output tsv `
        --only-show-errors 2>&1
    if ($LASTEXITCODE -eq 0) {
        $metadataCount = ([string]$result).Trim()
        if ($metadataCount -eq '0') {
            $script:LastSecretDiagnostic = 'No active secret metadata is present.'
            return 'absent'
        }
        if ($metadataCount -match '^[1-9][0-9]*$') {
            $script:LastSecretDiagnostic = 'Active secret metadata is present.'
            return 'present'
        }
        $script:LastSecretDiagnostic = "Unexpected metadata count '$metadataCount'."
        return 'error'
    }
    $script:LastSecretDiagnostic = ([string]$result).Trim()
    if ($script:LastSecretDiagnostic -match '(?i)SecretNotFound|was not found|could not be found') {
        return 'absent'
    }
    if ($script:LastSecretDiagnostic -match '(?i)Forbidden|ForbiddenByRbac') {
        return 'forbidden'
    }

    return 'error'
}

try {
    $secretState = ''
    for ($attempt = 1; $attempt -le $AuthorizationRetryCount; $attempt++) {
        $secretState = Get-LegacySecretState
        if ($secretState -ne 'forbidden') {
            break
        }
        if ($attempt -lt $AuthorizationRetryCount) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }

    if ($secretState -eq 'forbidden') {
        throw "Unable to verify the active legacy SQL connection-string secret after RBAC propagation: $LastSecretDiagnostic"
    }
    if ($secretState -eq 'error') {
        throw "Unable to verify the active legacy SQL connection-string secret: $LastSecretDiagnostic"
    }
    if ($secretState -eq 'present') {
        & az keyvault secret delete `
            --vault-name $LegacyVaultName `
            --name sql-connection-string `
            --output none `
            --only-show-errors
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to delete the active legacy SQL connection-string secret."
        }
    }

    $secretState = Get-LegacySecretState
    if ($secretState -ne 'absent') {
        throw "The active legacy SQL connection-string secret absence could not be verified: $LastSecretDiagnostic"
    }

    Write-Output "Verified that no active legacy SQL connection-string secret remains."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdRoleAssignmentId)) {
        & az role assignment delete `
            --ids $createdRoleAssignmentId `
            --only-show-errors
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to remove the temporary exact-secret cleanup role assignment.'
        }
    }
}
