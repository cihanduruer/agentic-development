[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int] $PullRequestNumber,

    [Parameter(Mandatory)]
    [string] $PullRequestTitle,

    [Parameter(Mandatory)]
    [string] $PullRequestBodyPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $HeadSha,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $KnowledgeRevision,

    [Parameter(Mandatory)]
    [string] $TestResultsPath,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [Parameter(Mandatory)]
    [ValidateSet('true', 'false')]
    [string] $ProductChange
)

$ErrorActionPreference = 'Stop'
$body = [IO.File]::ReadAllText((Resolve-Path $PullRequestBodyPath))
$failures = [Collections.Generic.List[string]]::new()

function Get-MarkdownSection {
    param(
        [Parameter(Mandatory)]
        [string] $Heading
    )

    $escapedHeading = [Regex]::Escape($Heading)
    $match = [Regex]::Match(
        $body,
        "(?ms)^##\s+$escapedHeading\s*\r?\n(?<content>.*?)(?=^##\s+|\z)")
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups['content'].Value.Trim()
}

$acceptanceEvidence = Get-MarkdownSection 'Acceptance criteria evidence'
$negativeEvidence = Get-MarkdownSection 'Negative-path evidence'
$knowledgeEvidence = Get-MarkdownSection 'Knowledge revision'

if ([string]::IsNullOrWhiteSpace($acceptanceEvidence) -or
    $acceptanceEvidence -notmatch '(?im)^\s*-\s*\[[xX]\]\s+\S') {
    $failures.Add('Acceptance criteria evidence must contain at least one completed checklist item.')
}

if ([string]::IsNullOrWhiteSpace($negativeEvidence) -or
    $negativeEvidence -notmatch '(?im)^\s*-\s+\S') {
    $failures.Add('Negative-path evidence must identify at least one negative path and its evidence.')
}

if ([string]::IsNullOrWhiteSpace($knowledgeEvidence) -or
    $knowledgeEvidence -notmatch '(?i)\b[0-9a-f]{40}\b') {
    $failures.Add('Knowledge revision must contain the full commit SHA used for grounding.')
}

if ($ProductChange -eq 'true' -and "$PullRequestTitle`n$body" -notmatch '\bAB#\d+\b') {
    $failures.Add('Product changes must reference an Azure Boards item as AB#<id>.')
}

$trxFiles = @(Get-ChildItem $TestResultsPath -Filter *.trx -Recurse)
if ($trxFiles.Count -eq 0) {
    $failures.Add('No TRX test result was produced.')
}

$testRuns = foreach ($trxFile in $trxFiles) {
    [xml] $trx = [IO.File]::ReadAllText($trxFile.FullName)
    $counters = $trx.TestRun.ResultSummary.Counters
    [pscustomobject] [ordered]@{
        file = $trxFile.Name
        total = [int] $counters.total
        executed = [int] $counters.executed
        passed = [int] $counters.passed
        failed = [int] $counters.failed
    }
}

if (@($testRuns).Count -gt 0) {
    if (($testRuns | Measure-Object total -Sum).Sum -eq 0) {
        $failures.Add('The QA test run executed zero tests.')
    }
    if (($testRuns | Measure-Object failed -Sum).Sum -gt 0) {
        $failures.Add('One or more QA tests failed.')
    }
}

$status = if ($failures.Count -eq 0) { 'passed' } else { 'failed' }
$result = [ordered]@{
    schemaVersion = 1
    status = $status
    pullRequest = $PullRequestNumber
    headSha = $HeadSha
    knowledgeRevision = $KnowledgeRevision
    agent = [ordered]@{
        profile = 'hotel-qa'
        execution = 'not-run'
        reason = 'GitHub does not expose a supported pull-request check API that dispatches this repository custom agent. This workflow independently enforces the profile evidence contract instead.'
    }
    acceptanceCriteriaEvidence = $acceptanceEvidence
    negativePathEvidence = $negativeEvidence
    testRuns = @($testRuns)
    failures = @($failures)
    completedAtUtc = [DateTime]::UtcNow.ToString('o')
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'qa-result.json')

$summary = @(
    '# Independent QA evidence'
    ''
    "- Result: **$status**"
    "- Pull request: #$PullRequestNumber"
    "- Commit: ``$HeadSha``"
    "- Knowledge revision: ``$KnowledgeRevision``"
    '- Custom agent execution: **not run**'
    ''
    'The workflow applied the `hotel-qa` evidence contract but did not invoke or impersonate the custom agent.'
    ''
    '## Test runs'
    ''
)
foreach ($testRun in $testRuns) {
    $summary += "- $($testRun.file): $($testRun.passed)/$($testRun.total) passed; $($testRun.failed) failed"
}
$summary += @('', '## Gate failures', '')
if ($failures.Count -eq 0) {
    $summary += '- None'
} else {
    $summary += $failures | ForEach-Object { "- $_" }
}
$summary | Set-Content (Join-Path $OutputDirectory 'qa-result.md')
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summary | ForEach-Object { $_ >> $env:GITHUB_STEP_SUMMARY }
}

if ($failures.Count -gt 0) {
    throw "QA evidence gate failed: $($failures -join ' ')"
}
