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
    $invalidManagedIdentitySettings = @(
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Server=other.database.windows.net;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Data Source=other.database.windows.net;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Database=other;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Authentication=Active Directory Interactive;',
        'Authentication=Active Directory Interactive;Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Integrated Security=True;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;malformed-tail',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;User=fixture;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Encrypt=True;Encrypt=False;',
        'Server=tcp:test.database.windows.net,1433;Initial Catalog=hotelbooking;Authentication=Active Directory Managed Identity;Connection Timeout=invalid;'
    )
    foreach ($invalidSetting in $invalidManagedIdentitySettings) {
        try {
            Get-State $invalidSetting
            throw "An ambiguous managed-identity SQL setting was accepted: $invalidSetting"
        }
        catch {
            if ($_.Exception.Message -match
                '^An ambiguous managed-identity SQL setting was accepted:') {
                throw
            }
        }
    }
    if ((Get-State 'database=hotelbooking;authentication=active directory managed identity;server=TCP:test.database.windows.net,1433;encrypt=TRUE;trustservercertificate=FALSE;') -ne 'managedIdentityExplicit') {
        throw 'A valid managed-identity setting with alternate order and casing was rejected.'
    }

    $modes = @('unconfigured', 'legacy', 'managedIdentityDefault', 'managedIdentityExplicit')
    $catalogStates = @($false, $true)
    $events = @('push', 'workflow_dispatch')
    $confirmations = @('', 'RECOVER-STRANDED-MANAGED-IDENTITY', 'recover-stranded-managed-identity')
    foreach ($mode in $modes) {
        foreach ($catalogReady in $catalogStates) {
            foreach ($eventName in $events) {
                foreach ($confirmation in $confirmations) {
                    $expected = if ($catalogReady) {
                        'ready'
                    }
                    elseif ($mode -in @('managedIdentityDefault', 'managedIdentityExplicit') -and
                        $eventName -eq 'workflow_dispatch' -and
                        $confirmation -ceq 'RECOVER-STRANDED-MANAGED-IDENTITY') {
                        'approvedRecovery'
                    }
                    else {
                        'blocked'
                    }
                    $actual = $null
                    $rejected = $false
                    $rejection = $null
                    try {
                        $actual = & $resolve `
                            -ConfiguredSqlMode $mode `
                            -CatalogReady $catalogReady `
                            -EventName $eventName `
                            -RecoveryConfirmation $confirmation
                    }
                    catch {
                        $rejected = $true
                        $rejection = $_
                    }
                    if ($expected -eq 'blocked') {
                        if (-not $rejected) {
                            throw "Recovery matrix unexpectedly allowed $mode/$catalogReady/$eventName/$confirmation."
                        }
                    }
                    elseif ($rejected) {
                        throw $rejection
                    }
                    elseif ($actual -ne $expected) {
                        throw "Recovery matrix mismatch for $mode/$catalogReady/$eventName/$confirmation."
                    }
                }
            }
        }
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
