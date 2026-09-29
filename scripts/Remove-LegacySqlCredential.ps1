param(
    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [string] $SqlServerName,

    [Parameter(Mandatory)]
    [string] $ApiAppName,

    [Parameter(Mandatory)]
    [string] $LegacyVaultName
)

$ErrorActionPreference = 'Stop'

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

$vaultCount = Invoke-AzTsv @(
    'keyvault', 'list',
    '--resource-group', $ResourceGroup,
    '--query', "[?name=='$LegacyVaultName'] | length(@)"
)
if ($vaultCount -eq '0') {
    Write-Output "Legacy Key Vault '$LegacyVaultName' is not deployed."
    exit 0
}
if ($vaultCount -ne '1') {
    throw "Unable to identify the legacy Key Vault '$LegacyVaultName'."
}

function Get-LegacySecretResourceId {
    $result = & az resource show `
        --resource-group $ResourceGroup `
        --resource-type Microsoft.KeyVault/vaults/secrets `
        --name "$LegacyVaultName/sql-connection-string" `
        --api-version 2024-11-01 `
        --query id `
        --output tsv `
        --only-show-errors 2>&1
    if ($LASTEXITCODE -eq 0) {
        return ([string]$result).Trim()
    }
    if (([string]$result) -match '(?i)ResourceNotFound|was not found|could not be found') {
        return ''
    }

    throw "Unable to verify the active legacy SQL connection-string secret: $result"
}

$secretResourceId = Get-LegacySecretResourceId
if (-not [string]::IsNullOrWhiteSpace($secretResourceId)) {
    & az resource delete `
        --ids $secretResourceId `
        --api-version 2024-11-01 `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to delete the active legacy SQL connection-string secret."
    }
}

if (-not [string]::IsNullOrWhiteSpace((Get-LegacySecretResourceId))) {
    throw "The active legacy SQL connection-string secret still exists after cleanup."
}

Write-Output "Verified that no active legacy SQL connection-string secret remains."
