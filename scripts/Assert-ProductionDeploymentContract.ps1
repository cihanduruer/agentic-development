param(
    [Parameter(Mandatory)]
    [string] $ReleaseDirectory
)

$ErrorActionPreference = 'Stop'
$contractPath = Join-Path $ReleaseDirectory 'deployment-contract.json'
$evidencePath = Join-Path $ReleaseDirectory 'release-evidence.json'
if (-not (Test-Path $contractPath) -or -not (Test-Path $evidencePath)) {
    throw 'Release artifact is older than the required production deployment contract.'
}

function Assert-JsonObjectRoot {
    param(
        [string] $Json,
        [string] $Name
    )

    $trimmed = $Json.Trim()
    if ($trimmed.Length -lt 2 -or
        $trimmed[0] -ne '{' -or
        $trimmed[$trimmed.Length - 1] -ne '}') {
        throw "$Name must have a JSON object root."
    }
}

try {
    $contractJson = Get-Content $contractPath -Raw
    $evidenceJson = Get-Content $evidencePath -Raw
    Assert-JsonObjectRoot $contractJson 'deployment-contract.json'
    Assert-JsonObjectRoot $evidenceJson 'release-evidence.json'
    $contract = $contractJson | ConvertFrom-Json
    $evidence = $evidenceJson | ConvertFrom-Json
}
catch {
    throw "Release deployment-contract metadata is malformed: $($_.Exception.Message)"
}

$contractSchemaIsInteger =
    $contract.schemaVersion -is [int] -or $contract.schemaVersion -is [long]
$contractVersionIsInteger =
    $contract.version -is [int] -or $contract.version -is [long]
$evidenceSchemaIsInteger =
    $evidence.schemaVersion -is [int] -or $evidence.schemaVersion -is [long]
$evidenceVersionIsInteger =
    $evidence.deploymentContractVersion -is [int] -or
    $evidence.deploymentContractVersion -is [long]

if (-not $contractSchemaIsInteger -or $contract.schemaVersion -ne 1 -or
    $contract.name -isnot [string] -or
    $contract.name -cne 'entra-sql-managed-identity' -or
    -not $contractVersionIsInteger -or $contract.version -ne 1 -or
    -not $evidenceSchemaIsInteger -or $evidence.schemaVersion -ne 2 -or
    $evidence.deploymentContract -isnot [string] -or
    $evidence.deploymentContract -cne $contract.name -or
    -not $evidenceVersionIsInteger -or
    $evidence.deploymentContractVersion -ne $contract.version) {
    throw 'Release artifact is older than the required production deployment contract.'
}

Write-Output "Verified production deployment contract '$($contract.name)' version $($contract.version)."
