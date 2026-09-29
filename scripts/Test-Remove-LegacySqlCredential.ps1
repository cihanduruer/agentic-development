$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Remove-LegacySqlCredential.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) "legacy-sql-cleanup-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $azPath = Join-Path $temp 'az.ps1'
    @'
$command = $args -join ' '
if ($command -match 'ad-only-auth get') { 'true'; exit 0 }
if ($command -match 'appsettings list') {
    'Server=tcp:test.database.windows.net,1433;Authentication=Active Directory Default;Encrypt=True;'
    exit 0
}
if ($command -match 'keyvault list') {
    '/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.KeyVault/vaults/test-kv'
    exit 0
}
if ($command -match 'role assignment list') { '0'; exit 0 }
if ($command -match 'role assignment create') {
    '/subscriptions/test/providers/Microsoft.Authorization/roleAssignments/test'
    exit 0
}
if ($command -match 'keyvault secret list-versions') {
    if (Test-Path $global:MockSecretStatePath) { 'SecretNotFound'; exit 1 }
    '1'
    exit 0
}
if ($command -match 'keyvault secret delete') {
    New-Item -ItemType File -Path $global:MockSecretStatePath -Force | Out-Null
    exit 0
}
if ($command -match 'role assignment delete') { exit 0 }
exit 1
'@ | Set-Content $azPath

    $statePath = Join-Path ([IO.Path]::GetTempPath()) 'legacy-sql-secret-deleted'
    Remove-Item $statePath -ErrorAction SilentlyContinue
    $global:MockAzPath = $azPath
    $global:MockSecretStatePath = $statePath
    function global:az {
        & $global:MockAzPath @args
    }

    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -SqlServerName test-sql `
            -ApiAppName test-api `
            -LegacyVaultName test-kv `
            -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d'
        throw 'Cleanup ran without explicit human approval.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'Explicit human approval') { throw }
    }
    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -SqlServerName test-sql `
            -ApiAppName test-api `
            -LegacyVaultName test-kv `
            -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d' `
            -Approved
        throw 'Cleanup ran without runtime readiness proof.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'Runtime readiness') { throw }
    }

    & $scriptPath `
        -ResourceGroup test-rg `
        -SqlServerName test-sql `
        -ApiAppName test-api `
        -LegacyVaultName test-kv `
        -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d' `
        -Approved `
        -RuntimeReadinessVerified
    if (-not (Test-Path $statePath)) {
        throw 'The exact legacy secret was not deleted.'
    }

    @'
$command = $args -join ' '
if ($command -match 'ad-only-auth get') { 'true'; exit 0 }
if ($command -match 'appsettings list') {
    'Server=tcp:test.database.windows.net,1433;Authentication=Active Directory Default;Encrypt=True;'
    exit 0
}
if ($command -match 'keyvault list') { ''; exit 0 }
exit 1
'@ | Set-Content $azPath
    & $scriptPath `
        -ResourceGroup test-rg `
        -SqlServerName test-sql `
        -ApiAppName test-api `
        -LegacyVaultName test-kv `
        -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d' `
        -Approved `
        -RuntimeReadinessVerified

    @'
$command = $args -join ' '
if ($command -match 'ad-only-auth get') { 'true'; exit 0 }
if ($command -match 'appsettings list') {
    'Server=tcp:test.database.windows.net;Authentication=Active Directory Default;' + 'Pass' + 'word=legacy;'
    exit 0
}
exit 1
'@ | Set-Content $azPath
    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -SqlServerName test-sql `
            -ApiAppName test-api `
            -LegacyVaultName test-kv `
            -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d' `
            -Approved `
            -RuntimeReadinessVerified
        throw 'A password-bearing runtime connection was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'expected passwordless') {
            throw
        }
    }

    @'
$command = $args -join ' '
if ($command -match 'ad-only-auth get') { 'true'; exit 0 }
if ($command -match 'appsettings list') {
    'Server=tcp:test.database.windows.net,1433;Authentication=Active Directory Default;Encrypt=True;'
    exit 0
}
if ($command -match 'keyvault list') {
    '/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.KeyVault/vaults/test-kv'
    exit 0
}
if ($command -match 'role assignment list') { '0'; exit 0 }
if ($command -match 'role assignment create') {
    '/subscriptions/test/providers/Microsoft.Authorization/roleAssignments/test'
    exit 0
}
if ($command -match 'keyvault secret list-versions') { 'ForbiddenByRbac'; exit 1 }
if ($command -match 'role assignment delete') { exit 0 }
exit 1
'@ | Set-Content $azPath
    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -SqlServerName test-sql `
            -ApiAppName test-api `
            -LegacyVaultName test-kv `
            -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d' `
            -Approved `
            -RuntimeReadinessVerified `
            -AuthorizationRetryCount 1
        throw 'Inaccessible secret state was incorrectly treated as absence.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'Unable to verify') {
            throw
        }
    }

    @'
$command = $args -join ' '
if ($command -match 'ad-only-auth get') { 'false'; exit 0 }
exit 1
'@ | Set-Content $azPath
    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -SqlServerName test-sql `
            -ApiAppName test-api `
            -LegacyVaultName test-kv `
            -DeploymentPrincipalObjectId '96a52cf4-fec7-4b1d-8a61-1b507a34d29d' `
            -Approved `
            -RuntimeReadinessVerified
        throw 'Cleanup unexpectedly ran before Entra-only SQL was verified.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'not Entra-only') {
            throw
        }
    }
}
finally {
    Remove-Item Function:\az -ErrorAction SilentlyContinue
    Remove-Variable MockAzPath -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable MockSecretStatePath -Scope Global -ErrorAction SilentlyContinue
    Remove-Item $statePath -ErrorAction SilentlyContinue
    Remove-Item $temp -Recurse -Force
}

Write-Output 'Legacy SQL credential cleanup tests passed.'
exit 0
