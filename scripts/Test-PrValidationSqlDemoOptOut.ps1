$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $repositoryRoot ".github/workflows/pr-validation.yml"
$workflowLines = [IO.File]::ReadAllLines($workflowPath)
$coverageScript = Join-Path $PSScriptRoot "Set-ValidationSqlCoverage.ps1"
$label = "demo-skip-sql-bootstrapper"
$cases = @(
    @{
        Name = "labeled pull request"
        Event = "pull_request"
        Labels = @($label, "other")
        ExpectedSkip = $true
    },
    @{
        Name = "unlabeled pull request"
        Event = "pull_request"
        Labels = @("other")
        ExpectedSkip = $false
    },
    @{
        Name = "main push with label-shaped input"
        Event = "push"
        Labels = @($label)
        ExpectedSkip = $false
    },
    @{
        Name = "manual dispatch targeting labeled pull request"
        Event = "workflow_dispatch"
        Labels = @($label)
        ExpectedSkip = $false
    }
)

$oldEvent = $env:GITHUB_EVENT_NAME
$oldLabels = $env:PR_LABELS_JSON
$oldOutput = $env:GITHUB_OUTPUT
$oldSummary = $env:GITHUB_STEP_SUMMARY
$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
try {
    foreach ($case in $cases) {
        $outputPath = Join-Path $tempDirectory "$($case.Name -replace '\W', '-').output"
        $summaryPath = Join-Path $tempDirectory "$($case.Name -replace '\W', '-').summary"
        $env:GITHUB_EVENT_NAME = $case.Event
        $env:PR_LABELS_JSON = ConvertTo-Json -InputObject @($case.Labels) -Compress
        $env:GITHUB_OUTPUT = $outputPath
        $env:GITHUB_STEP_SUMMARY = $summaryPath

        & $coverageScript

        $outputs = ConvertFrom-StringData ([IO.File]::ReadAllText($outputPath))
        $actualSkip = $outputs.skip -eq "true"
        if ($actualSkip -ne $case.ExpectedSkip) {
            throw "$($case.Name): expected skip=$($case.ExpectedSkip), got skip=$actualSkip."
        }

        $expectedCoverage = if ($case.ExpectedSkip) { "skipped-demo-opt-out" } else { "executed" }
        if ($outputs.coverage -ne $expectedCoverage) {
            throw "$($case.Name): expected coverage '$expectedCoverage', got '$($outputs.coverage)'."
        }

        $summary = [IO.File]::ReadAllText($summaryPath)
        $requiredSummaryText = if ($case.ExpectedSkip) {
            "intentionally skipped"
        } else {
            "enabled"
        }
        if ($summary -notmatch [regex]::Escape($requiredSummaryText)) {
            throw "$($case.Name): job summary does not report '$requiredSummaryText'."
        }
    }
} finally {
    $env:GITHUB_EVENT_NAME = $oldEvent
    $env:PR_LABELS_JSON = $oldLabels
    $env:GITHUB_OUTPUT = $oldOutput
    $env:GITHUB_STEP_SUMMARY = $oldSummary
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force
}

$sqlSteps = @(
    "Test SQL bootstrapper source mutations",
    "Test SQL bootstrapper evidence gate",
    "Test SQL bootstrapper evidence mutants",
    "Test SQL bootstrapper against SQL Server",
    "Verify SQL bootstrapper evidence"
)
foreach ($stepName in $sqlSteps) {
    $start = [Array]::FindIndex($workflowLines, [Predicate[string]]{
        param($line)
        $line -ceq "      - name: $stepName"
    })
    if ($start -lt 0) {
        throw "Missing SQL validation step '$stepName'."
    }

    $end = $start + 1
    while ($end -lt $workflowLines.Length -and $workflowLines[$end] -notmatch "^      - ") {
        $end++
    }

    $step = $workflowLines[$start..($end - 1)] -join "`n"
    if ($step -notmatch "(?m)^        if: steps\.sql-coverage\.outputs\.skip != 'true'$") {
        throw "SQL validation step '$stepName' must use the shared opt-out condition."
    }
}

$workflow = [IO.File]::ReadAllText($workflowPath)
if (-not $workflow.Contains('SQL_BOOTSTRAPPER_COVERAGE: ${{ steps.sql-coverage.outputs.coverage }}') -or
    -not $workflow.Contains('sqlBootstrapperCoverage = $env:SQL_BOOTSTRAPPER_COVERAGE')) {
    throw "Validation provenance must record whether SQL bootstrapper coverage was skipped."
}

Write-Output "PR validation SQL demo opt-out fixtures passed."
