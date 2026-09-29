[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $TrxPath
)

$ErrorActionPreference = 'Stop'
$expectedTotal = 22

if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
    throw "SQL bootstrapper TRX '$TrxPath' does not exist."
}

[xml] $trx = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $TrxPath))
$counters = $trx.TestRun.ResultSummary.Counters
if ($null -eq $counters) {
    throw "SQL bootstrapper TRX '$TrxPath' has no result counters."
}

function Get-Counter {
    param([Parameter(Mandatory)][string] $Name)

    $value = $counters.GetAttribute($Name)
    if ($value -notmatch '^\d+$') {
        throw "SQL bootstrapper TRX counter '$Name' is missing or invalid."
    }

    return [int] $value
}

$requiredCounters = @{
    total = $expectedTotal
    executed = $expectedTotal
    passed = $expectedTotal
    failed = 0
    error = 0
    timeout = 0
    aborted = 0
    inconclusive = 0
    passedButRunAborted = 0
    notRunnable = 0
    notExecuted = 0
    disconnected = 0
    warning = 0
    completed = 0
    inProgress = 0
    pending = 0
}
foreach ($entry in $requiredCounters.GetEnumerator()) {
    $actual = Get-Counter -Name $entry.Key
    if ($actual -ne $entry.Value) {
        throw "SQL bootstrapper TRX counter '$($entry.Key)' is $actual; expected $($entry.Value)."
    }
}

$results = @($trx.TestRun.Results.UnitTestResult)
if ($results.Count -ne $expectedTotal) {
    throw "SQL bootstrapper TRX contains $($results.Count) results; expected $expectedTotal."
}
if (@($results | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0) {
    throw 'SQL bootstrapper TRX contains a result that is not Passed.'
}

$expectedGroups = [ordered] @{
    ExactDirectPermissionContractMigratesAndRerunsIdempotently = 1
    MutatedDirectPermissionStateFailsClosed = 5
    UnexpectedRuntimeRoleOwnerFailsClosed = 2
    UnexpectedRuntimeRoleMemberFailsClosed = 1
    DelegatedRuntimeRolePermissionFailsClosed = 12
    FailureImmediatelyBeforeCommitRollsBackEveryMutation = 1
}
$testClassPrefix =
    'AgenticHotelBooking.IntegrationTests.' +
    'SqlManagedIdentityBootstrapperSqlServerTests.'
$methodPattern =
    '^' +
    [regex]::Escape($testClassPrefix) +
    '(?<Method>[A-Za-z_][A-Za-z0-9_]*)(?:\(.*\))?$'
$methodNames = foreach ($result in $results) {
    if ($result.testName -notmatch $methodPattern) {
        throw "SQL bootstrapper TRX contains unexpected test name '$($result.testName)'."
    }

    $Matches.Method
}
$actualGroups = @($methodNames | Group-Object)
if ($actualGroups.Count -ne $expectedGroups.Count) {
    throw "SQL bootstrapper TRX has $($actualGroups.Count) method groups; expected $($expectedGroups.Count)."
}
foreach ($entry in $expectedGroups.GetEnumerator()) {
    $matchingGroups = @(
        $actualGroups |
            Where-Object { $_.Name -eq $entry.Key }
    )
    $actual = if ($matchingGroups.Count -eq 1) {
        [int] $matchingGroups[0].Count
    }
    else {
        0
    }
    if ($actual -ne $entry.Value) {
        throw "SQL bootstrapper TRX group '$($entry.Key)' has $actual results; expected $($entry.Value)."
    }
}

Write-Host "SQL bootstrapper evidence passed: $expectedTotal exact tests executed and passed."
