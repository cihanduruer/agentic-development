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

try {
    $contract = Get-Content $contractPath -Raw | ConvertFrom-Json
    $evidence = Get-Content $evidencePath -Raw | ConvertFrom-Json
}
catch {
    throw "Release deployment-contract metadata is malformed: $($_.Exception.Message)"
}

if ($contract.schemaVersion -ne 1 -or
    $contract.name -ne 'entra-sql-managed-identity' -or
    $contract.version -ne 1 -or
    $evidence.schemaVersion -ne 2 -or
    $evidence.deploymentContract -ne $contract.name -or
    $evidence.deploymentContractVersion -ne $contract.version) {
    throw 'Release artifact is older than the required production deployment contract.'
}

Write-Output "Verified production deployment contract '$($contract.name)' version $($contract.version)."
