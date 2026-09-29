$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Invoke-KnowledgeIndexerWithRetry.ps1'

$state = @{ Elapsed = 0; Attempts = 0 }
$invokeIndexer = {
    param($IndexerArguments)
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
    param($IndexerArguments)
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
if ($state.Attempts -ne 41) {
    throw "Expected 41 bounded attempts but observed $($state.Attempts)."
}

Write-Output 'Knowledge indexer retry tests passed.'
exit 0
