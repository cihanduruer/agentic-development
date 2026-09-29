$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Invoke-KnowledgeIndexerWithRetry.ps1'

$state = @{ Elapsed = 0; Attempts = 0 }
$invokeIndexer = {
    param($IndexerArguments, $AttemptTimeoutSeconds)
    $state.Attempts++
    [pscustomobject]@{
        ExitCode = if ($state.Attempts -eq 40) { 0 } else { 1 }
        Output = if ($state.Attempts -eq 40) { 'indexed' } else { 'Forbidden' }
    }
}.GetNewClosure()
$sleep = { param($Seconds) $state.Elapsed += $Seconds }.GetNewClosure()
$getElapsed = { $state.Elapsed }.GetNewClosure()
$result = & $scriptPath `
    -Root docs/knowledge `
    -Endpoint https://example.search.windows.net `
    -Revision ('a' * 40) `
    -InvokeIndexer $invokeIndexer `
    -Sleep $sleep `
    -GetElapsedSeconds $getElapsed
if ($state.Attempts -ne 40 -or $state.Elapsed -ne 585 -or $result[-1] -ne 'indexed') {
    throw 'The retry helper did not tolerate a near-ten-minute RBAC propagation delay.'
}

$state = @{ Elapsed = 0; Attempts = 0 }
$invokeIndexer = {
    param($IndexerArguments, $AttemptTimeoutSeconds)
    $state.Attempts++
    [pscustomobject]@{ ExitCode = 1; Output = 'final authorization diagnostic' }
}.GetNewClosure()
$sleep = { param($Seconds) $state.Elapsed += $Seconds }.GetNewClosure()
$getElapsed = { $state.Elapsed }.GetNewClosure()
try {
    & $scriptPath `
        -Root docs/knowledge `
        -Endpoint https://example.search.windows.net `
        -Revision ('b' * 40) `
        -MaximumWaitSeconds 600 `
        -InvokeIndexer $invokeIndexer `
        -Sleep $sleep `
        -GetElapsedSeconds $getElapsed
    throw 'The retry helper unexpectedly succeeded after its bounded deadline.'
}
catch {
    if ($_.Exception.Message -notmatch '600s' -or
        $_.Exception.Message -notmatch 'final authorization diagnostic') {
        throw
    }
}
if ($state.Attempts -ne 40) {
    throw "Expected 40 bounded attempts but observed $($state.Attempts)."
}

$state = @{ Elapsed = 0 }
$invokeIndexer = {
    param($IndexerArguments, $AttemptTimeoutSeconds)
    $state.Elapsed = 900
    [pscustomobject]@{ ExitCode = 0; Output = 'late success'; TimedOut = $false }
}.GetNewClosure()
$getElapsed = { $state.Elapsed }.GetNewClosure()
try {
    & $scriptPath `
        -Root docs/knowledge `
        -Endpoint https://example.search.windows.net `
        -Revision ('c' * 40) `
        -MaximumWaitSeconds 600 `
        -InvokeIndexer $invokeIndexer `
        -Sleep { param($Seconds) } `
        -GetElapsedSeconds $getElapsed
    throw 'A child process that exceeded the overall deadline was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'exceeded the 600s' -or
        $_.Exception.Message -notmatch 'late success') {
        throw
    }
}

$state = @{ AttemptTimeout = 0 }
$invokeIndexer = {
    param($IndexerArguments, $AttemptTimeoutSeconds)
    $state.AttemptTimeout = $AttemptTimeoutSeconds
    [pscustomobject]@{ ExitCode = -1; Output = 'terminated child'; TimedOut = $true }
}.GetNewClosure()
try {
    & $scriptPath `
        -Root docs/knowledge `
        -Endpoint https://example.search.windows.net `
        -Revision ('d' * 40) `
        -MaximumWaitSeconds 600 `
        -InvokeIndexer $invokeIndexer `
        -Sleep { param($Seconds) } `
        -GetElapsedSeconds { 0 }
    throw 'A timed-out child process was accepted.'
}
catch {
    if ($_.Exception.Message -notmatch 'child-process deadline' -or
        $_.Exception.Message -notmatch 'terminated child') {
        throw
    }
}
if ($state.AttemptTimeout -ne 600) {
    throw "Expected a 600-second child deadline but observed $($state.AttemptTimeout)."
}

Write-Output 'Knowledge indexer retry tests passed.'
exit 0
