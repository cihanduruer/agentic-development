$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Assert-ProductionDeploymentContract.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) "deployment-contract-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $temp | Out-Null

function Assert-ProductionPolicyCheckout {
    param([string] $Workflow)
    $jobs = $Workflow -split '(?m)^  deploy:\s*$', 2
    if ($jobs.Count -ne 2 -or
        $jobs[0] -notmatch '(?m)^          ref: \$\{\{ github\.sha \}\}\s*$' -or
        $jobs[0] -match '(?m)^          ref: \$\{\{ inputs\.commit_sha \}\}\s*$' -or
        $jobs[1] -notmatch '(?m)^          ref: \$\{\{ inputs\.commit_sha \}\}\s*$') {
        throw 'Production preflight must use trusted dispatch policy, not the selected ancestor policy.'
    }
}

try {
    $workflow = Get-Content (Join-Path $PSScriptRoot '..\.github\workflows\deploy-production.yml') -Raw
    Assert-ProductionPolicyCheckout $workflow
    try {
        Assert-ProductionPolicyCheckout ($workflow.Replace('ref: ${{ github.sha }}', 'ref: ${{ inputs.commit_sha }}'))
        throw 'An ancestor-selected production validator was accepted.'
    }
    catch {
        if ($_.Exception.Message -notmatch 'trusted dispatch policy') { throw }
    }

    @{
        schemaVersion = 1
        name = 'entra-sql-managed-identity'
        version = 2
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'deployment-contract.json')
    @{
        schemaVersion = 2
        deploymentContract = 'entra-sql-managed-identity'
        deploymentContractVersion = 2
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'release-evidence.json')
    & $scriptPath -ReleaseDirectory $temp | Out-Null

    foreach ($oldVersion in @(0, 1)) {
        @{
            schemaVersion = 1
            name = 'entra-sql-managed-identity'
            version = $oldVersion
        } | ConvertTo-Json | Set-Content (Join-Path $temp 'deployment-contract.json')
        @{
            schemaVersion = 2
            deploymentContract = 'entra-sql-managed-identity'
            deploymentContractVersion = $oldVersion
        } | ConvertTo-Json | Set-Content (Join-Path $temp 'release-evidence.json')
        try {
            & $scriptPath -ReleaseDirectory $temp
            throw "A self-consistent obsolete version-$oldVersion release was accepted."
        }
        catch {
            if ($_.Exception.Message -notmatch 'older than') { throw }
        }
    }
    @{
        schemaVersion = 2
        deploymentContract = 'entra-sql-managed-identity'
        deploymentContractVersion = 2
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'release-evidence.json')

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
{"schemaVersion":$invalidValue,"name":"entra-sql-managed-identity","version":2}
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
        version = 2
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
        version = 2
    } | ConvertTo-Json | Set-Content (Join-Path $temp 'deployment-contract.json')
    foreach ($invalidValue in @('true', '"2"', '2.0')) {
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
        deploymentContractVersion = 2
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
