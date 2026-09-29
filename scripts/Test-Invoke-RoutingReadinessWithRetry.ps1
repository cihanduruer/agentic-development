$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-RoutingReadinessWithRetry.ps1')

$revision = '0123456789abcdef'
$body = @{
    correlationId = 'deployment-readiness'
    knowledgeRevision = $revision
} | ConvertTo-Json -Compress
$headers = @{ Authorization = 'Bearer test-token' }

$attempts = 0
$delayedSuccess = {
    param($Uri, $Body, $Headers, $TimeoutSeconds)
    $script:attempts++
    if ($script:attempts -lt 3) {
        return [pscustomobject]@{
            StatusCode = 200
            Content = '{"effectiveWorker":"human_review","model":"policy:deterministic","reason":"Microsoft safety or grounding evaluation failed. (evaluation_error)"}'
        }
    }
    [pscustomobject]@{
        StatusCode = 200
        Content = '{"effectiveWorker":"qa-agent","model":"policy:deterministic","reason":"passed"}'
    }
}

$result = Invoke-RoutingReadinessWithRetry `
    -Uri 'https://example.invalid/api/orchestration/route' `
    -Body $body `
    -Headers $headers `
    -ExpectedRevision $revision `
    -MaximumWaitSeconds 2 `
    -RetryDelaySeconds 0.01 `
    -RequestInvoker $delayedSuccess
if ($attempts -ne 3 -or $result -notmatch 'succeeded after 3 attempt') {
    throw 'Routing readiness did not preserve delayed success behavior.'
}

$script:simulatedElapsed = 0.0
$script:persistentAttempts = 0
$timeoutMessage = ''
try {
    Invoke-RoutingReadinessWithRetry `
        -Uri 'https://example.invalid/api/orchestration/route' `
        -Body $body `
        -Headers $headers `
        -ExpectedRevision $revision `
        -MaximumWaitSeconds 600 `
        -RetryDelaySeconds 15 `
        -RequestInvoker {
            $script:persistentAttempts++
            [pscustomobject]@{
                StatusCode = 200
                Content = '{"effectiveWorker":"human_review","model":"policy:deterministic","reason":"Microsoft safety or grounding evaluation failed. (evaluation_error)"}'
            }
        } `
        -ElapsedSecondsProvider { $script:simulatedElapsed } `
        -SleepAction { param($Milliseconds) $script:simulatedElapsed += $Milliseconds / 1000 }
}
catch {
    $timeoutMessage = $_.Exception.Message
}
if ($timeoutMessage -notmatch 'within 600 second' -or
    $timeoutMessage -notmatch 'exact revision' -or
    $timeoutMessage -notmatch "worker='human_review'" -or
    -not $timeoutMessage.Contains(
        "reason='Microsoft safety or grounding evaluation failed. (evaluation_error)'") -or
    $timeoutMessage -notmatch "correlation='deployment-readiness'" -or
    $persistentAttempts -ne 40) {
    throw "Routing readiness timeout diagnostics were incomplete: $timeoutMessage"
}

$script:simulatedElapsed = 0.0
$script:nearDeadlineAttempts = 0
$nearDeadline = Invoke-RoutingReadinessWithRetry `
    -Uri 'https://example.invalid/api/orchestration/route' `
    -Body $body `
    -Headers $headers `
    -ExpectedRevision $revision `
    -MaximumWaitSeconds 600 `
    -RetryDelaySeconds 15 `
    -RequestInvoker {
        $script:nearDeadlineAttempts++
        if ($script:nearDeadlineAttempts -lt 40) {
            return [pscustomobject]@{
                StatusCode = 200
                Content = '{"effectiveWorker":"human_review","model":"policy:deterministic","reason":"Microsoft safety or grounding evaluation failed. (evaluation_error)"}'
            }
        }
        [pscustomobject]@{
            StatusCode = 200
            Content = '{"effectiveWorker":"qa-agent","model":"policy:deterministic","reason":"passed"}'
        }
    } `
    -ElapsedSecondsProvider { $script:simulatedElapsed } `
    -SleepAction { param($Milliseconds) $script:simulatedElapsed += $Milliseconds / 1000 }
if ($simulatedElapsed -ne 585 -or $nearDeadline -notmatch 'succeeded after 40 attempt') {
    throw 'Routing readiness did not accept exact-revision success at 585 seconds.'
}

$script:simulatedElapsed = 599.0
$boundarySuccess = Invoke-RoutingReadinessWithRetry `
    -Uri 'https://example.invalid/api/orchestration/route' `
    -Body $body `
    -Headers $headers `
    -ExpectedRevision $revision `
    -MaximumWaitSeconds 600 `
    -RequestInvoker {
        [pscustomobject]@{
            StatusCode = 200
            Content = '{"effectiveWorker":"qa-agent","model":"policy:deterministic","reason":"passed"}'
        }
    } `
    -ElapsedSecondsProvider { $script:simulatedElapsed }
if ($boundarySuccess -notmatch 'succeeded after 1 attempt') {
    throw 'Routing readiness did not accept success inside the 600-second boundary.'
}

$script:simulatedElapsed = 599.0
$lateSuccessMessage = ''
try {
    Invoke-RoutingReadinessWithRetry `
        -Uri 'https://example.invalid/api/orchestration/route' `
        -Body $body `
        -Headers $headers `
        -ExpectedRevision $revision `
        -MaximumWaitSeconds 600 `
        -RetryDelaySeconds 15 `
        -RequestInvoker {
            $script:simulatedElapsed = 600.0
            [pscustomobject]@{
                StatusCode = 200
                Content = '{"effectiveWorker":"qa-agent","model":"policy:deterministic","reason":"passed"}'
            }
        } `
        -ElapsedSecondsProvider { $script:simulatedElapsed } `
        -SleepAction { param($Milliseconds) $script:simulatedElapsed += $Milliseconds / 1000 }
}
catch {
    $lateSuccessMessage = $_.Exception.Message
}
if ($lateSuccessMessage -notmatch 'after the overall deadline' -or
    $lateSuccessMessage -notmatch "reason='passed'") {
    throw "Routing readiness accepted or misdiagnosed a late success: $lateSuccessMessage"
}

$nonTransientMessage = ''
try {
    Invoke-RoutingReadinessWithRetry `
        -Uri 'https://example.invalid/api/orchestration/route' `
        -Body $body `
        -Headers $headers `
        -ExpectedRevision $revision `
        -MaximumWaitSeconds 600 `
        -RequestInvoker {
            [pscustomobject]@{
                StatusCode = 200
                Content = '{"effectiveWorker":"human_review","model":"policy:deterministic","reason":"prompt_attack"}'
            }
        }
}
catch {
    $nonTransientMessage = $_.Exception.Message
}
if ($nonTransientMessage -notmatch 'non-transient decision' -or
    $nonTransientMessage -notmatch "reason='prompt_attack'") {
    throw "Routing readiness retried or misdiagnosed a non-transient guardrail decision: $nonTransientMessage"
}

$bareSyntheticMessage = ''
try {
    Invoke-RoutingReadinessWithRetry `
        -Uri 'https://example.invalid/api/orchestration/route' `
        -Body $body `
        -Headers $headers `
        -ExpectedRevision $revision `
        -MaximumWaitSeconds 600 `
        -RequestInvoker {
            [pscustomobject]@{
                StatusCode = 200
                Content = '{"effectiveWorker":"human_review","model":"policy:deterministic","reason":"evaluation_error"}'
            }
        }
}
catch {
    $bareSyntheticMessage = $_.Exception.Message
}
if ($bareSyntheticMessage -notmatch 'non-transient decision') {
    throw 'Routing readiness accepted the old synthetic reason instead of the router reason-code contract.'
}

$script:simulatedElapsed = 599.0
$script:observedTimeout = 0
$hungMessage = ''
try {
    Invoke-RoutingReadinessWithRetry `
        -Uri 'https://example.invalid/api/orchestration/route' `
        -Body $body `
        -Headers $headers `
        -ExpectedRevision $revision `
        -MaximumWaitSeconds 600 `
        -RequestInvoker {
            param($Uri, $Body, $Headers, $TimeoutSeconds)
            $script:observedTimeout = $TimeoutSeconds
            throw 'request deadline exceeded'
        } `
        -ElapsedSecondsProvider { $script:simulatedElapsed }
}
catch {
    $hungMessage = $_.Exception.Message
}
if ($observedTimeout -ne 1 -or
    $hungMessage -notmatch 'non-transient request failure' -or
    $hungMessage -notmatch 'request deadline exceeded') {
    throw "Routing readiness did not cap or diagnose a hung request: $hungMessage"
}

$revisionMessage = ''
try {
    Invoke-RoutingReadinessWithRetry `
        -Uri 'https://example.invalid/api/orchestration/route' `
        -Body '{"correlationId":"deployment-readiness","knowledgeRevision":"wrong"}' `
        -Headers $headers `
        -ExpectedRevision $revision `
        -MaximumWaitSeconds 1 `
        -RequestInvoker { throw 'should not execute' }
}
catch {
    $revisionMessage = $_.Exception.Message
}
if ($revisionMessage -notmatch [regex]::Escape($revision)) {
    throw 'Routing readiness did not reject a mismatched knowledge revision before requesting.'
}

Write-Output 'Routing readiness retry tests passed.'
