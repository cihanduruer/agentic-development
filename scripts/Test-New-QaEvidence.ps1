$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PullRequestClassification.ps1"

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

        [string] $Title = 'AB#999 evidence change',

        [string[]] $ChangedFiles = @('src/Api/Program.cs'),

        [string] $Counters = 'total="2" executed="2" passed="2" failed="0"'
    )

    $casePath = Join-Path $testRoot $CaseName
    $resultsPath = Join-Path $casePath 'TestResults'
    $outputPath = Join-Path $casePath 'QaEvidence'
    New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
    $body | Set-Content (Join-Path $casePath 'body.md')
    $ChangedFiles | Set-Content (Join-Path $casePath 'changed-files.txt')
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
            -PullRequestTitle $Title `
            -PullRequestBodyPath (Join-Path $casePath 'body.md') `
            -HeadSha 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' `
            -TrustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' `
            -TestResultsPath $resultsPath `
            -OutputDirectory $outputPath `
            -ChangedFilesPath (Join-Path $casePath 'changed-files.txt')
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
    $qaWorkflow = [IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot '../.github/workflows/qa-evidence.yml'))
    $releaseWorkflow = [IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot '../.github/workflows/release-proposal.yml'))
    $productionWorkflow = [IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot '../.github/workflows/deploy-production.yml'))
    Assert-Equal (
        $qaWorkflow.Contains(
            'name: qa-evidence-${{ steps.context.outputs.head-sha }}-${{ steps.evidence.outputs.metadata-digest }}')
    ) $true 'QA upload must include the metadata digest in its artifact identity.'
    Assert-Equal (
        $releaseWorkflow.Contains('qaMetadataDigest = $env:QA_METADATA_DIGEST')
    ) $true 'Release evidence must preserve the QA metadata digest.'
    Assert-Equal (
        $productionWorkflow.Contains(
            'artifact.name === `qa-evidence-${process.env.COMMIT_SHA}-${evidence.qaMetadataDigest}`')
    ) $true 'Production preflight must require the digest-bound QA artifact.'

    $rawBody = "- Azure Boards: N/A`r`n- Platform change: true`r`nCaf$([char]0x00E9)"
    $serializedBodyPath = Join-Path $testRoot 'serialized-body.md'
    [IO.File]::WriteAllText(
        $serializedBodyPath,
        $rawBody,
        [Text.UTF8Encoding]::new($false))
    $serializedBody = [IO.File]::ReadAllText($serializedBodyPath)
    Assert-Equal $serializedBody $rawBody 'Workflow serialization must preserve PR body bytes.'
    Assert-Equal `
        (Get-PullRequestMetadataDigest -Title 'Harden platform' -Body $serializedBody) `
        (Get-PullRequestMetadataDigest -Title 'Harden platform' -Body $rawBody) `
        'Producer and consumer metadata digests must match.'

    $valid = Invoke-Evidence -CaseName 'valid' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

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
    Assert-Equal (
        $valid.Result.metadataDigest -match '^[0-9a-f]{64}$'
    ) $true 'QA evidence must bind a SHA-256 digest of the reviewed title and body.'

    $placeholder = Invoke-Evidence -CaseName 'placeholder' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

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
## Work tracking

- Azure Boards: AB#999
- Platform change: false

## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Missing evidence is rejected by scripts/Test-New-QaEvidence.ps1.

## Knowledge revision

Compared `bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb` with `cccccccccccccccccccccccccccccccccccccccc`.
'@
    Assert-Equal $ambiguousRevision.Result.status 'failed' 'Ambiguous knowledge revisions must fail.'
    Assert-Equal ($null -eq $ambiguousRevision.Failure) $false 'Ambiguous knowledge revisions must throw.'

    $nonProduct = Invoke-Evidence -CaseName 'non-product' -Title 'Harden platform paths' -Body @'
## Work tracking

- Azure Boards: N/A
- Platform change: true

# Pipeline change
'@
    Assert-Equal $nonProduct.Failure $null 'Non-product evidence should pass without product sections.'
    Assert-Equal $nonProduct.Result.knowledgeRevisionEvidence 'N/A - non-product change.' 'Non-product evidence should record an explicit N/A.'
    Assert-Equal $nonProduct.Result.trustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' 'Non-product evidence should retain the trusted revision.'
    Assert-Equal $nonProduct.Result.classification 'platform' 'Platform src changes should retain truthful platform classification.'

    $missingIdentity = Invoke-Evidence -CaseName 'missing-identity' -Title 'Change product behavior' -Body @'
## Work tracking

- Platform change: false

## Acceptance criteria evidence

- [x] Product behavior is covered.

## Negative-path evidence

- Missing identity is rejected.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@
    Assert-Equal $missingIdentity.Result.status 'failed' 'Product src changes without AB identity must fail.'
    Assert-Equal ($null -eq $missingIdentity.Failure) $false 'Missing product identity must throw.'

    $contradictoryPlatform = Invoke-Evidence -CaseName 'contradictory-platform' -Title 'Harden platform paths' -Body @'
## Work tracking

- Azure Boards: N/A
- Platform change: false
- Platform change: true
'@
    Assert-Equal $contradictoryPlatform.Result.status 'failed' 'Contradictory platform declarations must fail.'
    Assert-Equal ($null -eq $contradictoryPlatform.Failure) $false 'Contradictory platform declarations must throw.'

    $missingPlatform = Invoke-Evidence -CaseName 'missing-platform' -Body @'
## Work tracking

- Azure Boards: AB#999
'@
    Assert-Equal $missingPlatform.Result.status 'failed' 'Missing platform declarations must fail.'
    Assert-Equal ($null -eq $missingPlatform.Failure) $false 'Missing platform declarations must throw.'

    $invalidPlatform = Invoke-Evidence -CaseName 'invalid-platform' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: yes
'@
    Assert-Equal $invalidPlatform.Result.status 'failed' 'Invalid platform declarations must fail.'
    Assert-Equal ($null -eq $invalidPlatform.Failure) $false 'Invalid platform declarations must throw.'

    $conflictingIdentity = Invoke-Evidence -CaseName 'conflicting-identity' -Body @'
## Work tracking

- Azure Boards: N/A
- Azure Boards: AB#999
- Platform change: true
'@
    Assert-Equal $conflictingIdentity.Result.status 'failed' 'Azure Boards N/A plus AB identity must fail.'
    Assert-Equal ($null -eq $conflictingIdentity.Failure) $false 'Conflicting Azure Boards declarations must throw.'

    $mixedIdentity = Invoke-Evidence -CaseName 'mixed-identity' -Title 'Harden platform paths' -Body @'
## Work tracking

- Azure Boards: N/A; AB#999
- Platform change: true
'@
    Assert-Equal $mixedIdentity.Result.status 'failed' 'Mixed N/A and AB identity must fail.'
    Assert-Equal ($null -eq $mixedIdentity.Failure) $false 'Mixed N/A and AB identity must throw.'

    $zeroTests = Invoke-Evidence `
        -CaseName 'zero-tests' `
        -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

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
## Work tracking

- Azure Boards: AB#999
- Platform change: false

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
