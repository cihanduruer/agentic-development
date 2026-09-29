param(
    [Parameter(Mandatory)]
    [ValidateSet('unconfigured', 'legacy', 'managedIdentityDefault', 'managedIdentityExplicit')]
    [string] $ConfiguredSqlMode,

    [Parameter(Mandatory)]
    [bool] $CatalogReady,

    [Parameter(Mandatory)]
    [string] $EventName,

    [string] $RecoveryConfirmation,

    [string] $FailureDiagnostic = 'No catalog diagnostic was captured.'
)

$ErrorActionPreference = 'Stop'
if ($CatalogReady) {
    Write-Output 'ready'
    exit 0
}

$managedIdentityState = $ConfiguredSqlMode -in @(
    'managedIdentityDefault',
    'managedIdentityExplicit'
)
$approvedRecovery = $EventName -eq 'workflow_dispatch' -and
    $RecoveryConfirmation -ceq 'RECOVER-STRANDED-MANAGED-IDENTITY'
if ($managedIdentityState -and $approvedRecovery) {
    Write-Output 'approvedRecovery'
    exit 0
}

throw "The pre-cutover SQL-backed catalog is not ready for state '$ConfiguredSqlMode'. A stranded managed-identity setting requires an explicitly confirmed manual recovery run. Diagnostic: $FailureDiagnostic"
