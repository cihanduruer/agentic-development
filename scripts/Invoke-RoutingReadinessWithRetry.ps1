function Invoke-RoutingReadinessWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Uri,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Body,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$ExpectedRevision,

        [ValidateRange(1, 3600)]
        [int]$MaximumWaitSeconds = 600,

        [ValidateRange(0.01, 60)]
        [double]$RetryDelaySeconds = 15,

        [scriptblock]$RequestInvoker,

        [scriptblock]$ElapsedSecondsProvider,

        [scriptblock]$SleepAction
    )

    $request = $Body | ConvertFrom-Json
    if ($request.knowledgeRevision -ne $ExpectedRevision) {
        throw "Routing readiness requires exact knowledge revision '$ExpectedRevision'."
    }
    if ([string]::IsNullOrWhiteSpace($request.correlationId)) {
        throw 'Routing readiness requires a correlation ID for bounded attempt persistence.'
    }

    if ($null -eq $RequestInvoker) {
        $RequestInvoker = {
            param($RequestUri, $RequestBody, $RequestHeaders, $TimeoutSeconds)
            Invoke-WebRequest `
                -Method POST `
                -Uri $RequestUri `
                -Body $RequestBody `
                -Headers $RequestHeaders `
                -ContentType 'application/json' `
                -SkipHttpErrorCheck `
                -OperationTimeoutSeconds $TimeoutSeconds
        }
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    if ($null -eq $ElapsedSecondsProvider) {
        $ElapsedSecondsProvider = { $stopwatch.Elapsed.TotalSeconds }
    }
    if ($null -eq $SleepAction) {
        $SleepAction = {
            param($Milliseconds)
            Start-Sleep -Milliseconds $Milliseconds
        }
    }

    $attempt = 0
    $lastDiagnostic = 'No routing request was attempted.'

    while ($true) {
        $elapsedSeconds = [double](& $ElapsedSecondsProvider)
        $remainingSeconds = $MaximumWaitSeconds - $elapsedSeconds
        if ($remainingSeconds -lt 1) {
            break
        }

        $attempt++
        $requestTimeout = [Math]::Min(30, [Math]::Floor($remainingSeconds))

        try {
            $response = & $RequestInvoker $Uri $Body $Headers $requestTimeout
        }
        catch {
            throw (
                "Routing readiness encountered a non-transient request failure for revision " +
                "'$ExpectedRevision', correlation '$($request.correlationId)': $($_.Exception.Message)")
        }

        $statusCode = [int]$response.StatusCode
        if ($statusCode -ne 200) {
            throw (
                "Routing readiness encountered non-transient HTTP $statusCode for revision " +
                "'$ExpectedRevision', correlation '$($request.correlationId)'.")
        }

        try {
                $decision = $response.Content | ConvertFrom-Json
        }
        catch {
            throw (
                "Routing readiness received malformed HTTP 200 JSON for revision '$ExpectedRevision', " +
                "correlation '$($request.correlationId)': $($_.Exception.Message)")
        }

        $lastDiagnostic = (
            "HTTP 200; worker='$($decision.effectiveWorker)'; model='$($decision.model)'; " +
            "reason='$($decision.reason)'; revision='$ExpectedRevision'; " +
            "correlation='$($request.correlationId)'")
        $reasonCodeMatch = [regex]::Match(
            [string]$decision.reason,
            '\((?<code>[a-z][a-z0-9_]*)\)\s*$',
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        $reasonCode = if ($reasonCodeMatch.Success) {
            $reasonCodeMatch.Groups['code'].Value
        }
        else {
            ''
        }
        if ($decision.effectiveWorker -eq 'qa-agent' -and
            $decision.model -match '^policy:') {
            if ([double](& $ElapsedSecondsProvider) -lt $MaximumWaitSeconds) {
                Write-Output (
                    "Routing readiness succeeded after $attempt attempt(s) for revision " +
                    "'$ExpectedRevision', correlation '$($request.correlationId)'; " +
                    "worker=$($decision.effectiveWorker); model=$($decision.model).")
                return
            }
            $lastDiagnostic = "$lastDiagnostic; qa-agent arrived after the overall deadline"
        }
        elseif ($decision.effectiveWorker -ne 'human_review' -or
            $reasonCode -ne 'evaluation_error') {
            throw "Routing readiness encountered a non-transient decision. $lastDiagnostic"
        }

        $remainingMilliseconds = [Math]::Floor(
            ($MaximumWaitSeconds - [double](& $ElapsedSecondsProvider)) * 1000)
        if ($remainingMilliseconds -le 0) {
            break
        }

        $delayMilliseconds = [Math]::Min(
            [Math]::Ceiling($RetryDelaySeconds * 1000),
            $remainingMilliseconds)
        & $SleepAction $delayMilliseconds
    }

    throw (
        "Routing readiness did not return qa-agent within $MaximumWaitSeconds second(s) " +
        "for exact revision '$ExpectedRevision' after $attempt attempt(s). Last result: $lastDiagnostic.")
}
