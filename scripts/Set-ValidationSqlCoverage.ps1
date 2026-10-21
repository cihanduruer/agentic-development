$labels = @()
if (-not [string]::IsNullOrWhiteSpace($env:PR_LABELS_JSON)) {
    $labels = @($env:PR_LABELS_JSON | ConvertFrom-Json)
}

$skip = $env:GITHUB_EVENT_NAME -ceq "pull_request" -and
    ($labels -ccontains "demo-skip-sql-bootstrapper")
$coverage = if ($skip) { "skipped-demo-opt-out" } else { "executed" }

"skip=$($skip.ToString().ToLowerInvariant())" |
    Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
"coverage=$coverage" |
    Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append

if ($skip) {
    "SQL bootstrapper validation: intentionally skipped by the `demo-skip-sql-bootstrapper` label. This artifact is not full SQL evidence." |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
} else {
    "SQL bootstrapper validation: enabled for this event." |
        Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}
