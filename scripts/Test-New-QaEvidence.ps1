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
        [string] $CaseName,

        [ValidateSet('true', 'false')]
        [string] $ProductChange = 'true',

        [string] $Counters = 'total="2" executed="2" passed="2" failed="0"'
    )

    $casePath = Join-Path $testRoot $CaseName
    $resultsPath = Join-Path $casePath 'TestResults'
    $outputPath = Join-Path $casePath 'QaEvidence'
    New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
    $body | Set-Content (Join-Path $casePath 'body.md')
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  <ResultSummary>
    <Counters $Counters />
  </ResultSummary>
</TestRun>
"@ | Set-Content (Join-Path $resultsPath 'results.trx')

    $failure = $null
    try {
        & "$PSScriptRoot/New-QaEvidence.ps1" `
            -PullRequestNumber 9 `
            -PullRequestTitle 'AB#999 evidence change' `
            -PullRequestBodyPath (Join-Path $casePath 'body.md') `
            -HeadSha 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' `
            -TrustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' `
            -TestResultsPath $resultsPath `
            -OutputDirectory $outputPath `
            -ProductChange $ProductChange
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
    Assert-Equal $valid.Result.trustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' 'The trusted revision should be preserved separately.'

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

    $nonProduct = Invoke-Evidence -CaseName 'non-product' -ProductChange false -Body '# Pipeline change'
    Assert-Equal $nonProduct.Failure $null 'Non-product evidence should pass without product sections.'
    Assert-Equal $nonProduct.Result.knowledgeRevisionEvidence 'N/A - non-product change.' 'Non-product evidence should record an explicit N/A.'
    Assert-Equal $nonProduct.Result.trustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' 'Non-product evidence should retain the trusted revision.'

    $zeroTests = Invoke-Evidence `
        -CaseName 'zero-tests' `
        -Body @'
## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Zero test runs are rejected.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@ `
        -Counters 'total="0" executed="0" passed="0" failed="0"'
    Assert-Equal $zeroTests.Result.status 'failed' 'Zero executed tests must fail.'
    Assert-Equal ($null -eq $zeroTests.Failure) $false 'Zero executed tests must throw.'

    $failedTests = Invoke-Evidence `
        -CaseName 'failed-tests' `
        -Body @'
## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Failed tests are rejected.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@ `
        -Counters 'total="2" executed="2" passed="1" failed="1"'
    Assert-Equal $failedTests.Result.status 'failed' 'Any non-passing test must fail.'
    Assert-Equal ($null -eq $failedTests.Failure) $false 'Any non-passing test must throw.'
}
finally {
    Remove-Item $testRoot -Recurse -Force
}

Write-Output 'New-QaEvidence tests passed.'
