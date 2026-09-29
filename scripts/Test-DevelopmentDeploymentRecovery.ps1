$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$classify = Join-Path $PSScriptRoot 'Get-AppSqlConfigurationState.ps1'
$resolve = Join-Path $PSScriptRoot 'Resolve-DevelopmentPreCutoverState.ps1'
$protect = Join-Path $PSScriptRoot 'Protect-DeploymentSecretFile.ps1'
$remove = Join-Path $PSScriptRoot 'Remove-DeploymentSecretFiles.ps1'
$server = 'test.database.windows.net'
$database = 'hotelbooking'
$temp = Join-Path ([IO.Path]::GetTempPath()) "deployment-recovery-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $temp | Out-Null

function Get-State([string] $ConnectionString) {
    $env:APP_SQL_CONNECTION_STRING = $ConnectionString
    try {
        return & $classify -ExpectedServer $server -ExpectedDatabase $database
    }
    finally {
        Remove-Item Env:\APP_SQL_CONNECTION_STRING -ErrorAction SilentlyContinue
    }
}

try {
    if ((Get-State '') -ne 'unconfigured') {
        throw 'A fresh environment was not classified as unconfigured.'
    }
    if ((Get-State 'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;User ID=legacy;Password=value;') -ne 'legacy') {
        throw 'A legacy SQL setting was not classified as legacy.'
    }
    if ((Get-State 'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Default;Encrypt=True;') -ne 'managedIdentityDefault') {
        throw 'The stranded default-chain setting was not classified for recovery.'
    }
    if ((Get-State 'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Encrypt=True;') -ne 'managedIdentityExplicit') {
        throw 'The explicit managed-identity setting was not classified for recovery.'
    }
    try {
        Get-State 'Server=tcp:other.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Default;'
        throw 'An unexpected SQL target was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'expected server and database') { throw }
    }
    try {
        Get-State 'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Default;User ID=legacy;'
        throw 'An ambiguous mixed authentication setting was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'ambiguously combines') { throw }
    }

    if ((& $resolve `
            -ConfiguredSqlMode legacy `
            -CatalogReady $true `
            -EventName push) -ne 'ready') {
        throw 'A healthy legacy upgrade did not retain the normal guarded path.'
    }
    if ((& $resolve `
            -ConfiguredSqlMode unconfigured `
            -CatalogReady $true `
            -EventName push) -ne 'ready') {
        throw 'A healthy fresh environment did not retain the normal path.'
    }
    foreach ($mode in @('managedIdentityDefault', 'managedIdentityExplicit')) {
        if ((& $resolve `
                -ConfiguredSqlMode $mode `
                -CatalogReady $false `
                -EventName workflow_dispatch `
                -RecoveryConfirmation RECOVER-STRANDED-MANAGED-IDENTITY) -ne
            'approvedRecovery') {
            throw "The approved stranded state '$mode' was not recoverable."
        }
        try {
            & $resolve `
                -ConfiguredSqlMode $mode `
                -CatalogReady $false `
                -EventName push
            throw "The stranded state '$mode' was recovered without manual approval."
        }
        catch {
            if ($_.Exception.Message -notmatch 'explicitly confirmed') { throw }
        }
    }
    try {
        & $resolve `
            -ConfiguredSqlMode legacy `
            -CatalogReady $false `
            -EventName workflow_dispatch `
            -RecoveryConfirmation RECOVER-STRANDED-MANAGED-IDENTITY
        throw 'An unhealthy legacy state bypassed the existing readiness guard.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'explicitly confirmed') { throw }
    }

    $protected = Join-Path $temp 'protected.txt'
    Set-Content -Path $protected -Value 'secret' -NoNewline
    function global:chmod { $global:LASTEXITCODE = 0 }
    & $protect -Path $protected
    if (-not (Test-Path $protected)) {
        throw 'Successful permission protection removed the file.'
    }

    $permissionFailure = Join-Path $temp 'permission-failure.txt'
    Set-Content -Path $permissionFailure -Value 'secret' -NoNewline
    function global:chmod { $global:LASTEXITCODE = 1 }
    try {
        & $protect -Path $permissionFailure
        throw 'Permission failure did not stop deployment.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'restrict deployment secret file permissions') { throw }
    }
    if (Test-Path $permissionFailure) {
        throw 'Permission failure retained the unprotected secret file.'
    }

    $first = Join-Path $temp 'first.txt'
    $second = Join-Path $temp 'second.txt'
    Set-Content -Path $first -Value 'secret' -NoNewline
    Set-Content -Path $second -Value 'diagnostic' -NoNewline
    & $remove -Path @($first, $second)
    if ((Test-Path $first) -or (Test-Path $second)) {
        throw 'Successful cleanup retained a deployment secret file.'
    }
    & $remove -Path @($first, $second)

    $cleanupFailure = Join-Path $temp 'cleanup-failure.txt'
    Set-Content -Path $cleanupFailure -Value 'secret' -NoNewline
    function global:Remove-Item {
        param([string] $LiteralPath, [switch] $Force, $ErrorAction)
        throw 'simulated deletion denial'
    }
    try {
        & $remove -Path $cleanupFailure
        throw 'Deletion denial did not fail cleanup.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'simulated deletion denial') { throw }
    }
}
finally {
    Microsoft.PowerShell.Management\Remove-Item Function:\chmod -ErrorAction SilentlyContinue
    Microsoft.PowerShell.Management\Remove-Item Function:\Remove-Item -ErrorAction SilentlyContinue
    Microsoft.PowerShell.Management\Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    Microsoft.PowerShell.Management\Remove-Item Env:\APP_SQL_CONNECTION_STRING -ErrorAction SilentlyContinue
}

Write-Output 'Development deployment recovery tests passed.'
exit 0
