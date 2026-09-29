$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        $Actual,

        [Parameter(Mandatory)]
        [AllowNull()]
        $Expected,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Invoke-Evidence {
    param(
        [Parameter(Mandatory)]
        [string] $Body,

        [Parameter(Mandatory)]
        [string] $CaseName
    )

    $casePath = Join-Path $testRoot $CaseName
    $resultsPath = Join-Path $casePath 'TestResults'
    $outputPath = Join-Path $casePath 'QaEvidence'
    New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
    $body | Set-Content (Join-Path $casePath 'body.md')
    @'
<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  <ResultSummary>
    <Counters total="2" executed="2" passed="2" failed="0" />
  </ResultSummary>
</TestRun>
'@ | Set-Content (Join-Path $resultsPath 'results.trx')

    $failure = $null
    try {
        & "$PSScriptRoot/New-QaEvidence.ps1" `
            -PullRequestNumber 9 `
            -PullRequestTitle 'Platform evidence change' `
            -PullRequestBodyPath (Join-Path $casePath 'body.md') `
            -HeadSha 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' `
            -TestResultsPath $resultsPath `
            -OutputDirectory $outputPath `
            -ProductChange false
    }
    catch {
        $failure = $_
    }

    [pscustomobject]@{
        Failure = $failure
        Result = Get-Content (Join-Path $outputPath 'qa-result.json') -Raw | ConvertFrom-Json
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "New-QaEvidence-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $valid = Invoke-Evidence -CaseName 'valid' -Body @'
## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Missing evidence is rejected by scripts/Test-New-QaEvidence.ps1.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@
    Assert-Equal $valid.Failure $null 'Valid evidence should pass.'
    Assert-Equal $valid.Result.status 'passed' 'Valid evidence should be recorded as passed.'
    Assert-Equal $valid.Result.knowledgeRevision 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' 'The cited knowledge revision should be preserved.'

    $placeholder = Invoke-Evidence -CaseName 'placeholder' -Body @'
## Acceptance criteria evidence

- [x] Criterion and evidence location

## Negative-path evidence

- Negative path and test or other evidence

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@
    Assert-Equal $placeholder.Result.status 'failed' 'Template placeholders must fail.'
    Assert-Equal ($null -eq $placeholder.Failure) $false 'Template placeholders must throw.'

    $ambiguousRevision = Invoke-Evidence -CaseName 'ambiguous-revision' -Body @'
## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Missing evidence is rejected by scripts/Test-New-QaEvidence.ps1.

## Knowledge revision

Compared `bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb` with `cccccccccccccccccccccccccccccccccccccccc`.
'@
    Assert-Equal $ambiguousRevision.Result.status 'failed' 'Ambiguous knowledge revisions must fail.'
    Assert-Equal ($null -eq $ambiguousRevision.Failure) $false 'Ambiguous knowledge revisions must throw.'
}
finally {
    Remove-Item $testRoot -Recurse -Force
}

Write-Output 'New-QaEvidence tests passed.'
