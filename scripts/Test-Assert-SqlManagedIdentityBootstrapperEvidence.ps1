$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assertionScript = Join-Path $PSScriptRoot 'Assert-SqlManagedIdentityBootstrapperEvidence.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "sql-bootstrap-evidence-$([guid]::NewGuid().ToString('N'))"
$shellPath = (Get-Process -Id $PID).Path

function New-TestTrx {
    param(
        [Parameter(Mandatory)]
        [string] $Path,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]] $TestNames,

        [int] $Executed,

        [int] $Passed,

        [int] $NotExecuted,

        [string] $Outcome = 'Passed'
    )

    $results = foreach ($testName in $TestNames) {
        $escapedName = [Security.SecurityElement]::Escape($testName)
        "<UnitTestResult testName=`"$escapedName`" outcome=`"$Outcome`" />"
    }
    $total = $TestNames.Count
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  <Results>
    $($results -join [Environment]::NewLine)
  </Results>
  <ResultSummary>
    <Counters total="$total" executed="$Executed" passed="$Passed" failed="0"
      error="0" timeout="0" aborted="0" inconclusive="0"
      passedButRunAborted="0" notRunnable="0" notExecuted="$NotExecuted"
      disconnected="0" warning="0" completed="0" inProgress="0"
      pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -LiteralPath $Path
}

function Invoke-Assertion {
    param([Parameter(Mandatory)][string] $Path)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $shellPath -NoProfile -File $assertionScript -TrxPath $Path 2>&1
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return @{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

$testClassPrefix =
    'AgenticHotelBooking.IntegrationTests.' +
    'SqlManagedIdentityBootstrapperSqlServerTests.'
$testNames = @(
    "${testClassPrefix}ExactDirectPermissionContractMigratesAndRerunsIdempotently"
    1..5 | ForEach-Object {
        "${testClassPrefix}MutatedDirectPermissionStateFailsClosed($_)"
    }
    1..2 | ForEach-Object {
        "${testClassPrefix}UnexpectedRuntimeRoleOwnerFailsClosed($_)"
    }
    "${testClassPrefix}UnexpectedRuntimeRoleMemberFailsClosed"
    1..12 | ForEach-Object {
        "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed($_)"
    }
    "${testClassPrefix}FailureImmediatelyBeforeCommitRollsBackEveryMutation"
)

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $validPath = Join-Path $testRoot 'valid.trx'
    New-TestTrx -Path $validPath -TestNames $testNames -Executed 22 -Passed 22 -NotExecuted 0
    $valid = Invoke-Assertion -Path $validPath
    if ($valid.ExitCode -ne 0) {
        throw "Valid SQL evidence was rejected: $($valid.Output)"
    }

    $zeroPath = Join-Path $testRoot 'zero.trx'
    New-TestTrx -Path $zeroPath -TestNames @() -Executed 0 -Passed 0 -NotExecuted 0
    $zero = Invoke-Assertion -Path $zeroPath
    if ($zero.ExitCode -eq 0) {
        throw 'Zero-executed SQL evidence was accepted.'
    }

    $bypassPath = Join-Path $testRoot 'bypass.trx'
    New-TestTrx `
        -Path $bypassPath `
        -TestNames $testNames `
        -Executed 0 `
        -Passed 0 `
        -NotExecuted 22 `
        -Outcome 'NotExecuted'
    $bypass = Invoke-Assertion -Path $bypassPath
    if ($bypass.ExitCode -eq 0) {
        throw 'NotExecuted SQL evidence was accepted.'
    }

    $wrongNamesPath = Join-Path $testRoot 'wrong-names.trx'
    $wrongNames = @($testNames)
    $wrongNames[-1] = 'UnexpectedReplacementTest'
    New-TestTrx `
        -Path $wrongNamesPath `
        -TestNames $wrongNames `
        -Executed 22 `
        -Passed 22 `
        -NotExecuted 0
    $wrongNamesResult = Invoke-Assertion -Path $wrongNamesPath
    if ($wrongNamesResult.ExitCode -eq 0) {
        throw 'SQL evidence with a missing required test group was accepted.'
    }
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

Write-Host 'SQL bootstrapper evidence assertion tests passed.'
exit 0
