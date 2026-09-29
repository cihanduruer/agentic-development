$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Assert-ProductionDeploymentContract.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) "deployment-contract-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    @{
        schemaVersion = 1
        name = 'entra-sql-managed-identity'
        version = 1
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'deployment-contract.json')
    @{
        schemaVersion = 2
        deploymentContract = 'entra-sql-managed-identity'
        deploymentContractVersion = 1
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'release-evidence.json')
    & $scriptPath -ReleaseDirectory $temp | Out-Null

    Remove-Item (Join-Path $temp 'deployment-contract.json')
    try {
        & $scriptPath -ReleaseDirectory $temp
        throw 'A release without a deployment contract was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'older than') { throw }
    }

    '{invalid-json' | Set-Content (Join-Path $temp 'deployment-contract.json')
    try {
        & $scriptPath -ReleaseDirectory $temp
        throw 'A malformed deployment contract was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'malformed') { throw }
    }

    @{
        schemaVersion = 1
        name = 'entra-sql-managed-identity'
        version = 0
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'deployment-contract.json')
    try {
        & $scriptPath -ReleaseDirectory $temp
        throw 'An obsolete deployment contract was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'older than') { throw }
    }

    foreach ($invalidValue in @('true', '"1"', '1.0')) {
        @"
{"schemaVersion":$invalidValue,"name":"entra-sql-managed-identity","version":1}
"@ | Set-Content (Join-Path $temp 'deployment-contract.json')
        try {
            & $scriptPath -ReleaseDirectory $temp
            throw "A deployment contract with invalid schema type '$invalidValue' was accepted."
        }
        catch {
            if ($_.Exception.Message -notmatch 'older than') { throw }
        }
    }

    $singletonContract = @{
        schemaVersion = 1
        name = 'entra-sql-managed-identity'
        version = 1
    } | ConvertTo-Json -Compress
    "[$singletonContract]" | Set-Content (Join-Path $temp 'deployment-contract.json')
    try {
        & $scriptPath -ReleaseDirectory $temp
        throw 'A singleton-array deployment contract was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'object root') { throw }
    }

    @{
        schemaVersion = 1
        name = 'entra-sql-managed-identity'
        version = 1
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'deployment-contract.json')
    foreach ($invalidValue in @('true', '"1"', '1.0')) {
        @"
{"schemaVersion":2,"deploymentContract":"entra-sql-managed-identity","deploymentContractVersion":$invalidValue}
"@ | Set-Content (Join-Path $temp 'release-evidence.json')
        try {
            & $scriptPath -ReleaseDirectory $temp
            throw "Release evidence with invalid contract-version type '$invalidValue' was accepted."
        }
        catch {
            if ($_.Exception.Message -notmatch 'older than') { throw }
        }
    }

    $singletonEvidence = @{
        schemaVersion = 2
        deploymentContract = 'entra-sql-managed-identity'
        deploymentContractVersion = 1
    } | ConvertTo-Json -Compress
    "[$singletonEvidence]" | Set-Content (Join-Path $temp 'release-evidence.json')
    try {
        & $scriptPath -ReleaseDirectory $temp
        throw 'Singleton-array release evidence was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'object root') { throw }
    }
}
finally {
    Remove-Item $temp -Recurse -Force
}

Write-Output 'Production deployment contract tests passed.'
exit 0
