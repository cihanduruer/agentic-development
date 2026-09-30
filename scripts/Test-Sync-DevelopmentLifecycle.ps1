$ErrorActionPreference = "Stop"

. "$PSScriptRoot/Sync-DevelopmentLifecycle.ps1" -DeploymentRunId 1 -DeployedSha ("a" * 40)

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$ExpectedMessage)
    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected '$ExpectedMessage', but no error was thrown."
}

$deployedSha = "a" * 40
$headSha = "13932fac3407bdb33f9408647e08b85cfc3cb123"
$repository = "cihanduruer/agentic-development"
$deployment = [pscustomobject]@{
    conclusion = "success"
    path = ".github/workflows/deploy-development.yml"
    head_branch = "main"
    head_sha = $deployedSha
    on_main = $true
    html_url = "https://github.com/$repository/actions/runs/100"
}
$pull = [pscustomobject]@{
    number = 6
    title = "AB#959: Show total stay price"
    body = @"
- Azure Boards: AB#959
- Platform change: false

Evidence for AB#959.
"@
    merged_at = "2026-09-29T08:00:00Z"
    merge_commit_sha = $deployedSha
    html_url = "https://github.com/$repository/pull/6"
    base = [pscustomobject]@{
        ref = "main"
        repo = [pscustomobject]@{ full_name = $repository }
    }
    head = [pscustomobject]@{
        sha = $headSha
        ref = "copilot/ab-960-apply-brand-color-palette"
        repo = [pscustomobject]@{ full_name = $repository }
    }
}
$metadataDigest = Get-PullRequestMetadataDigest -Title $pull.title -Body $pull.body
$issue = [pscustomobject]@{
    id = 500
    number = 5
    title = "[AB#959] Show total stay price"
    body = "Azure Boards work item: [AB#959](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/959)"
    author_association = "OWNER"
}
$validation = [pscustomobject]@{
    conclusion = "success"
    path = ".github/workflows/pr-validation.yml"
    event = "push"
    head_branch = "main"
    head_sha = $deployedSha
    created_at = "2026-09-29T08:01:00Z"
    html_url = "https://github.com/$repository/actions/runs/101"
}
$qa = [pscustomobject]@{
    conclusion = "success"
    path = ".github/workflows/qa-evidence.yml"
    head_sha = "c" * 40
    created_at = "2026-09-29T08:02:00Z"
    html_url = "https://github.com/$repository/actions/runs/102"
    artifacts = @([pscustomobject]@{
        name = "qa-evidence-$deployedSha-$metadataDigest"
        expired = $false
    })
}
$reviewRun = [pscustomobject]@{
    id = 103
    conclusion = "success"
    path = ".github/workflows/hotel-code-review.yml"
    event = "pull_request_target"
    head_sha = $headSha
    head_branch = $pull.head.ref
    repository = [pscustomobject]@{ full_name = $repository }
    head_repository = [pscustomobject]@{ full_name = $repository }
    created_at = "2026-09-30T09:45:04Z"
    updated_at = "2026-09-30T09:48:48Z"
    html_url = "https://github.com/$repository/actions/runs/103"
    pull_requests = @([pscustomobject]@{ number = 6 })
}
$review = [pscustomobject]@{
    user = [pscustomobject]@{ login = "copilot-pull-request-reviewer[bot]" }
    commit_id = $headSha
    submitted_at = "2026-09-30T09:48:39Z"
}
$reviewRunJobs = @([pscustomobject]@{
    run_id = $reviewRun.id
    jobs = @([pscustomobject]@{
        conclusion = "success"
        steps = @(
            [pscustomobject]@{ name = "Request Copilot review"; conclusion = "success" }
            [pscustomobject]@{
                name = "Wait for current-head review and evaluate findings"
                conclusion = "success"
            }
        )
    })
})
$arguments = @{
    ExpectedSha = $deployedSha
    Deployment = $deployment
    PullRequests = @($pull)
    Issues = @($issue)
    ValidationRuns = @($validation)
    QaRuns = @($qa)
    ReviewRuns = @($reviewRun)
    ReviewRunJobs = $reviewRunJobs
    Reviews = @($review)
    Organization = "ai-enabled-ado-org"
    Project = "sample-project"
    Repository = $repository
}

$evidence = Resolve-LifecycleEvidence @arguments
Assert-True ($evidence.WorkItemId -eq 959) "The trusted AB#959 evidence should resolve."
Assert-True (
    @(Get-PullRequestAbIds `
        -Title "Document the pipeline" `
        -Body "- Platform change: true`nHistorical example: AB#959").Count -eq 0
) "Incidental PR body references must not become delivery identity."
Assert-True (
    @(Get-PullRequestAbIds `
        -Title "Deliver pricing" `
        -Body "- Azure Boards: AB#959`n- Platform change: false").Count -eq 1
) "The explicit Azure Boards tracking field should provide delivery identity."

$laterReviewRun = $reviewRun.PSObject.Copy()
$laterReviewRun.created_at = "2026-09-29T08:01:00Z"
$laterReviewRun.updated_at = "2026-09-29T08:02:00Z"
$laterReviewRun.html_url = "https://github.com/$repository/actions/runs/104"
$repeatedReviewArguments = $arguments.Clone()
$repeatedReviewArguments.ReviewRuns = @($reviewRun, $laterReviewRun)
$repeatedReviewEvidence = Resolve-LifecycleEvidence @repeatedReviewArguments
Assert-True (
    $repeatedReviewEvidence.ReviewUrl -eq $reviewRun.html_url
) "Review evidence should select a run containing the exact-head review."

$emptyAssociationRun = $reviewRun.PSObject.Copy()
$emptyAssociationRun.pull_requests = @()
$emptyAssociationArguments = $arguments.Clone()
$emptyAssociationArguments.ReviewRuns = @($emptyAssociationRun)
$emptyAssociationEvidence = Resolve-LifecycleEvidence @emptyAssociationArguments
Assert-True (
    $emptyAssociationEvidence.ReviewUrl -eq $reviewRun.html_url
) "A trusted exact-head review workflow with an empty PR association should resolve."

function Assert-ReviewEvidenceRejected {
    param(
        [Parameter(Mandatory)][hashtable]$Changes,
        [Parameter(Mandatory)][string]$Message
    )

    $caseArguments = $arguments.Clone()
    foreach ($key in $Changes.Keys) {
        $caseArguments[$key] = $Changes[$key]
    }
    Assert-Throws { Resolve-LifecycleEvidence @caseArguments } $Message
}

$wrongReviewShaRun = $emptyAssociationRun.PSObject.Copy()
$wrongReviewShaRun.head_sha = "d" * 40
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($wrongReviewShaRun)
} "no successful review workflow contains a Copilot review"

$wrongReviewBranchRun = $emptyAssociationRun.PSObject.Copy()
$wrongReviewBranchRun.head_branch = "other-branch"
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($wrongReviewBranchRun)
} "no successful review workflow contains a Copilot review"

$wrongReviewRepositoryRun = $emptyAssociationRun.PSObject.Copy()
$wrongReviewRepositoryRun.repository = [pscustomobject]@{ full_name = "another/repository" }
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($wrongReviewRepositoryRun)
} "no successful review workflow contains a Copilot review"

$wrongReviewHeadRepositoryRun = $emptyAssociationRun.PSObject.Copy()
$wrongReviewHeadRepositoryRun.head_repository = [pscustomobject]@{ full_name = "another/repository" }
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($wrongReviewHeadRepositoryRun)
} "no successful review workflow contains a Copilot review"

$wrongReviewEventRun = $emptyAssociationRun.PSObject.Copy()
$wrongReviewEventRun.event = "pull_request"
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($wrongReviewEventRun)
} "no successful review workflow contains a Copilot review"

$conflictingAssociationRun = $reviewRun.PSObject.Copy()
$conflictingAssociationRun.pull_requests = @(
    [pscustomobject]@{ number = 6 }
    [pscustomobject]@{ number = 7 }
)
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($conflictingAssociationRun)
} "no successful review workflow contains a Copilot review"

$missingRepositoryRun = $emptyAssociationRun.PSObject.Copy()
$missingRepositoryRun.head_repository = $null
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($missingRepositoryRun)
} "no successful review workflow contains a Copilot review"

$unrelatedReview = $review.PSObject.Copy()
$unrelatedReview.user = [pscustomobject]@{ login = "another-reviewer[bot]" }
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($emptyAssociationRun)
    Reviews = @($unrelatedReview)
} "no successful review workflow contains a Copilot review"

Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($emptyAssociationRun)
    Reviews = @()
} "no successful review workflow contains a Copilot review"

$staleReview = $review.PSObject.Copy()
$staleReview.submitted_at = "2026-09-30T09:48:49Z"
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($emptyAssociationRun)
    Reviews = @($staleReview)
} "no successful review workflow contains a Copilot review"

$skippedReviewJob = [pscustomobject]@{
    run_id = $reviewRun.id
    jobs = @([pscustomobject]@{
        conclusion = "success"
        steps = @([pscustomobject]@{
            name = "Identify hotel change"
            conclusion = "success"
        })
    })
}
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($emptyAssociationRun)
    ReviewRunJobs = @($skippedReviewJob)
} "no successful review workflow contains a Copilot review"

Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($emptyAssociationRun)
    ReviewRunJobs = @($reviewRunJobs[0], $reviewRunJobs[0])
} "no successful review workflow contains a Copilot review"

$failedReviewRun = $emptyAssociationRun.PSObject.Copy()
$failedReviewRun.conclusion = "failure"
Assert-ReviewEvidenceRejected @{
    ReviewRuns = @($failedReviewRun)
} "no successful review workflow contains a Copilot review"

$staleDeployment = $deployment.PSObject.Copy()
$staleDeployment.head_sha = "c" * 40
$staleArguments = $arguments.Clone()
$staleArguments.Deployment = $staleDeployment
Assert-Throws { Resolve-LifecycleEvidence @staleArguments } "exact main SHA"

$missingQaArguments = $arguments.Clone()
$missingQaArguments.QaRuns = @()
Assert-Throws { Resolve-LifecycleEvidence @missingQaArguments } "Evidence pending: no successful unexpired QA evidence"
Assert-True (
    Test-RetryableEvidenceError -Message "Evidence pending: QA is still running."
) "Missing asynchronous evidence should be retryable."
Assert-True (
    -not (Test-RetryableEvidenceError -Message "Pull request identity is ambiguous.")
) "Hard evidence failures must not be retried."

$platformPull = $pull.PSObject.Copy()
$platformPull.title = "Harden platform workflow"
$platformPull.body = "- Platform change: true"
$platformArguments = $arguments.Clone()
$platformArguments.PullRequests = @($platformPull)
$platformResult = Resolve-LifecycleEvidence @platformArguments
Assert-True ($platformResult.Action -eq "Skipped") "A non-AB platform PR should skip clearly."
Assert-True (
    $platformResult.PullRequestNumber -eq $platformPull.number -and
    $platformResult.DeployedSha -eq $deployedSha
) "Every skipped result should retain PR and deployed-SHA provenance."

$historicalReferencePull = $pull.PSObject.Copy()
$historicalReferencePull.title = "Document the development pipeline"
$historicalReferencePull.body = @"
- Platform change: true
Azure Boards: N/A; AB#959 is a closed historical demonstration, not new product scope.
"@
$historicalReferenceArguments = $arguments.Clone()
$historicalReferenceArguments.PullRequests = @($historicalReferencePull)
$historicalReferenceResult = Resolve-LifecycleEvidence @historicalReferenceArguments
Assert-True (
    $historicalReferenceResult.Action -eq "Skipped"
) "A platform/N/A PR must not treat a historical AB reference as delivery scope."

$conflictingIdentityPull = $pull.PSObject.Copy()
$conflictingIdentityPull.title = "Platform delivery"
$conflictingIdentityPull.body = @"
- Platform change: true
- Azure Boards: N/A
- Azure Boards: AB#959
"@
$conflictingIdentityArguments = $arguments.Clone()
$conflictingIdentityArguments.PullRequests = @($conflictingIdentityPull)
$conflictingValues = @(
    Get-PullRequestFieldValues `
        -Body $conflictingIdentityPull.body `
        -Field 'Azure Boards'
)
Assert-True (
    @($conflictingValues | Where-Object {
        $_ -match '^N/A(?:\s*[.;]|$)'
    }).Count -eq 1
) "The conflict fixture should declare Azure Boards N/A."
Assert-True (
    @(Get-AbIds -Text $conflictingIdentityPull.body).Count -eq 1
) "The conflict fixture should declare one explicit AB identity."
Assert-Throws {
    Resolve-LifecycleEvidence @conflictingIdentityArguments
} "conflicting Azure Boards N/A and AB identity declarations"

$contradictoryPlatformPull = $pull.PSObject.Copy()
$contradictoryPlatformPull.title = "Platform delivery"
$contradictoryPlatformPull.body = @"
- Azure Boards: N/A
- Platform change: false
- Platform change: true
"@
$contradictoryPlatformArguments = $arguments.Clone()
$contradictoryPlatformArguments.PullRequests = @($contradictoryPlatformPull)
Assert-Throws {
    Resolve-LifecycleEvidence @contradictoryPlatformArguments
} "contradictory Platform change declarations"
Assert-Throws {
    Get-PullRequestAbIds `
        -Title $contradictoryPlatformPull.title `
        -Body $contradictoryPlatformPull.body
} "contradictory Platform change declarations"
Assert-Throws {
    Get-PullRequestClassification `
        -Title 'Change product behavior' `
        -Body '- Azure Boards: AB#959'
} "must declare '- Platform change: true' or '- Platform change: false'"
Assert-Throws {
    Get-PullRequestClassification `
        -Title 'Change product behavior' `
        -Body "- Azure Boards: AB#959`n- Platform change: yes"
} "must be exactly 'true' or 'false'"
Assert-Throws {
    Get-PullRequestClassification `
        -Title 'Harden platform paths' `
        -Body "- Azure Boards: N/A; AB#959`n- Platform change: true"
} "conflicting Azure Boards N/A and AB identity declarations"

$untrackedPull = $pull.PSObject.Copy()
$untrackedPull.title = "Change delivery behavior"
$untrackedPull.body = "- Platform change: false"
$untrackedArguments = $arguments.Clone()
$untrackedArguments.PullRequests = @($untrackedPull)
Assert-Throws {
    Resolve-LifecycleEvidence @untrackedArguments
} "not explicitly marked as a platform change"

$ambiguousPull = $pull.PSObject.Copy()
$ambiguousPull.title = "AB#959 and AB#960"
$ambiguousArguments = $arguments.Clone()
$ambiguousArguments.PullRequests = @($ambiguousPull)
Assert-Throws { Resolve-LifecycleEvidence @ambiguousArguments } "exactly one Azure Boards item"

$wrongPullArguments = $arguments.Clone()
$wrongPullArguments.ExpectedPullRequestNumber = 7
Assert-Throws { Resolve-LifecycleEvidence @wrongPullArguments } "not selected PR #7"

$closedItem = [pscustomobject]@{
    rev = 4
    fields = [pscustomobject]@{
        "System.State" = "Closed"
        "System.Tags" = "github-synced; ready-for-triage; github-synced"
        "System.TeamProject" = "sample-project"
        "System.WorkItemType" = "User Story"
    }
    relations = @()
}
Assert-EligibleWorkItem -WorkItem $closedItem -Project "sample-project"
$patch = @(New-WorkItemEvidencePatch -WorkItem $closedItem -Evidence $evidence)
Assert-True ($patch.Count -eq 8) "Closed AB#959 should receive tags, five links, history, and revision test."
Assert-True (-not ($patch.path -contains "/fields/System.State")) "Lifecycle sync must never change terminal state."
Assert-True (
    ($patch | Where-Object path -eq "/fields/System.Tags").value -eq
        "github-synced; delivery-evidence") "Tag hygiene should remove ready-for-triage."
$comparisonUri = Get-DeployedToMainComparisonUri `
    -Repository $repository `
    -DeployedSha $deployedSha
Assert-True (
    $comparisonUri -eq "https://api.github.com/repos/$repository/compare/$deployedSha...main"
) "The comparison must use deployed SHA as base and main as head."
Assert-True (Test-CommitOnMain -ComparisonStatus "identical") "The exact main tip should be accepted."
Assert-True (
    Test-CommitOnMain -ComparisonStatus "ahead"
) "For deployed-base to main-head comparison, a deployed main ancestor should be accepted."
Assert-True (
    -not (Test-CommitOnMain -ComparisonStatus "behind")
) "For deployed-base to main-head comparison, a commit ahead of main must fail closed."
Assert-True (-not (Test-CommitOnMain -ComparisonStatus "diverged")) "A diverged commit must fail closed."

$existingRelations = @(
    $evidence.PullRequestUrl,
    $evidence.DeploymentUrl,
    $evidence.ValidationUrl,
    $evidence.QaUrl,
    $evidence.ReviewUrl
) | ForEach-Object {
    [pscustomobject]@{ rel = "Hyperlink"; url = $_ }
}
$replayedItem = [pscustomobject]@{
    rev = 5
    fields = [pscustomobject]@{
        "System.State" = "Closed"
        "System.Tags" = "github-synced; delivery-evidence"
    }
    relations = $existingRelations
}
$replayPatch = @(New-WorkItemEvidencePatch -WorkItem $replayedItem -Evidence $evidence)
Assert-True ($replayPatch.Count -eq 0) "An evidence-complete replay must be a no-op."

Assert-Throws { Assert-WorkItemUpdateStatus -StatusCode 412 } "revision conflict"

$wrongType = $closedItem.PSObject.Copy()
$wrongType.fields = $closedItem.fields.PSObject.Copy()
$wrongType.fields."System.WorkItemType" = "Task"
Assert-Throws {
    Assert-EligibleWorkItem -WorkItem $wrongType -Project "sample-project"
} "not an eligible User Story or Bug"

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    if ($Uri -match '/actions/artifacts\?name=qa-evidence-') {
        return [pscustomobject]@{
            artifacts = @([pscustomobject]@{
                name = "qa-evidence-$deployedSha-$metadataDigest"
                expired = $false
                workflow_run = [pscustomobject]@{ id = 42 }
            })
        }
    }
    if ($Uri -match '/actions/runs/42$') {
        return [pscustomobject]@{
            id = 42
            conclusion = "success"
            path = ".github/workflows/qa-evidence.yml"
        }
    }
    if ($Uri -match '/search/issues\?q=') {
        return [pscustomobject]@{
            total_count = 1
            incomplete_results = $false
            items = @([pscustomobject]@{ id = 500; number = 5 })
        }
    }
    if ($Uri -match 'page=1') {
        return ,@(
            [pscustomobject]@{ number = 5 },
            [pscustomobject]@{ number = 6 }
        )
    }
    return ,@()
}
$pagedIssues = @(Get-GitHubPages -Uri "https://api.github.com/repos/example/issues?state=all")
Assert-True ($pagedIssues.Count -eq 2) "GitHub array responses must be flattened."
Assert-True ($script:pageRequestCount -eq 1) "A short GitHub page must stop pagination."

$script:pageRequestCount = 0
$targetedQa = @(
    Get-QaEvidenceRuns `
        -Repository $repository `
        -ExpectedSha $deployedSha `
        -MetadataDigest $metadataDigest
)
Assert-True ($targetedQa.Count -eq 1) "Exact-name QA artifact lookup should return its workflow run."
Assert-True ($script:pageRequestCount -eq 2) "QA lookup should use one artifact and one run request."

$script:pageRequestCount = 0
$targetedIssues = @(Get-CanonicalIssueCandidates -Repository $repository -Ids @(959))
Assert-True ($targetedIssues.Count -eq 1) "Targeted AB issue lookup should return search candidates."
Assert-True ($script:pageRequestCount -eq 1) "AB issue lookup should use one targeted search request."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    return [pscustomobject]@{
        total_count = 1
        incomplete_results = $true
        items = @([pscustomobject]@{ id = 500; number = 5 })
    }
}
Assert-Throws {
    Get-CanonicalIssueCandidates -Repository $repository -Ids @(959)
} "reported incomplete results"
Assert-True ($script:pageRequestCount -eq 1) "Incomplete search results must fail on the first page."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    return [pscustomobject]@{
        total_count = 2
        incomplete_results = $false
        items = @()
    }
}
Assert-Throws {
    Get-CanonicalIssueCandidates -Repository $repository -Ids @(959)
} "returned truncated results"
Assert-True ($script:pageRequestCount -eq 1) "Truncated search results must fail without pointless paging."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    $page = if ($Uri -match '[?&]page=(?<page>\d+)') { [int]$Matches.page } else { 1 }
    $items = if ($page -eq 1) {
        @(1..100 | ForEach-Object { [pscustomobject]@{ id = $_; number = $_ } })
    } else {
        @([pscustomobject]@{ id = 101; number = 101 })
    }
    return [pscustomobject]@{
        total_count = 101
        incomplete_results = $false
        items = $items
    }
}
$pagedSearch = @(Get-CanonicalIssueCandidates -Repository $repository -Ids @(959))
Assert-True ($pagedSearch.Count -eq 101) "Targeted issue search must collect every reported result."
Assert-True ($script:pageRequestCount -eq 2) "A 101-result issue search must request its second page."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    $page = if ($Uri -match '[?&]page=(?<page>\d+)') { [int]$Matches.page } else { 1 }
    return [pscustomobject]@{
        total_count = if ($page -eq 1) { 101 } else { 102 }
        incomplete_results = $false
        items = @(1..100 | ForEach-Object { [pscustomobject]@{ id = $_; number = $_ } })
    }
}
Assert-Throws {
    Get-CanonicalIssueCandidates -Repository $repository -Ids @(959)
} "changed while results were paged"
Assert-True ($script:pageRequestCount -eq 2) "Changing search totals must fail on the changed page."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    return [pscustomobject]@{
        total_count = 1001
        incomplete_results = $false
        items = @()
    }
}
Assert-Throws {
    Get-CanonicalIssueCandidates -Repository $repository -Ids @(959)
} "exceeds the 1,000-result completeness limit"
Assert-True ($script:pageRequestCount -eq 1) "Searches above GitHub's result cap must fail immediately."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    return [pscustomobject]@{
        total_count = 1
        incomplete_results = $false
        items = @(
            [pscustomobject]@{ id = 1; number = 1 }
            [pscustomobject]@{ id = 2; number = 2 }
        )
    }
}
Assert-Throws {
    Get-CanonicalIssueCandidates -Repository $repository -Ids @(959)
} "returned more unique results than total_count"
Assert-True ($script:pageRequestCount -eq 1) "Over-complete search results must fail immediately."

$script:pageRequestCount = 0
function Invoke-GitHubApi {
    param([string]$Uri)
    $script:pageRequestCount++
    return [pscustomobject]@{
        total_count = 101
        incomplete_results = $false
        items = @(1..100 | ForEach-Object { [pscustomobject]@{
            id = $_
            number = $_
        } })
    }
}
Assert-Throws {
    Get-CanonicalIssueCandidates -Repository $repository -Ids @(959)
} "returned duplicate issue ID"
Assert-True ($script:pageRequestCount -eq 2) "Duplicate paginated issue IDs must fail on the repeated page."

$changedMetadataPull = $pull.PSObject.Copy()
$changedMetadataPull.body = "$($pull.body)`nMetadata changed after QA."
$changedMetadataArguments = $arguments.Clone()
$changedMetadataArguments.PullRequests = @($changedMetadataPull)
Assert-Throws {
    Resolve-LifecycleEvidence @changedMetadataArguments
} "no successful unexpired QA evidence"

$script:boundaryMode = 'platform'
$script:azureCalls = 0
$originalGitHubToken = $env:GITHUB_TOKEN
$env:GITHUB_TOKEN = 'test-token'
$platformBoundaryPull = $platformPull.PSObject.Copy()
$platformBoundaryPull.body = @"
- Azure Boards: N/A
- Platform change: true
"@

function az {
    $script:azureCalls++
    throw 'Azure must not be called by boundary failure/skip tests.'
}

function Invoke-GitHubApi {
    param([string]$Uri)

    if ($Uri -match '/actions/runs/100$') {
        return $deployment.PSObject.Copy()
    }
    if ($Uri -match '/compare/') {
        return [pscustomobject]@{ status = 'identical' }
    }
    if ($Uri -match '/commits/.+/pulls\?') {
        $selectedPull = if ($script:boundaryMode -eq 'platform') {
            $platformBoundaryPull
        } else {
            $pull
        }
        return ,@($selectedPull)
    }
    if ($Uri -match '/search/issues\?') {
        return [pscustomobject]@{
            total_count = 1
            incomplete_results = $false
            items = @($issue)
        }
    }
    if ($Uri -match '/actions/artifacts\?') {
        if ($script:boundaryMode -eq 'platform') {
            return [pscustomobject]@{ artifacts = @() }
        }
        return [pscustomobject]@{
            artifacts = @([pscustomobject]@{
                name = "qa-evidence-$deployedSha-$metadataDigest"
                expired = $false
                workflow_run = [pscustomobject]@{ id = 42 }
            })
        }
    }
    if ($Uri -match '/actions/runs/42$') {
        return $qa
    }
    if ($Uri -match '/actions/runs/103/jobs\?') {
        return [pscustomobject]@{
            total_count = 1
            jobs = $reviewRunJobs[0].jobs
        }
    }
    if ($Uri -match '/actions/workflows/pr-validation\.yml/runs') {
        $runs = if ($script:boundaryMode -eq 'platform') { @() } else { @($validation) }
        return [pscustomobject]@{ workflow_runs = $runs }
    }
    if ($Uri -match '/actions/workflows/hotel-code-review\.yml/runs') {
        $runs = if ($script:boundaryMode -eq 'platform') { @() } else { @($reviewRun) }
        return [pscustomobject]@{ workflow_runs = $runs }
    }
    if ($Uri -match '/pulls/6/reviews\?') {
        return ,@()
    }
    throw "Unexpected GitHub API request in boundary test: $Uri"
}

$platformBoundaryResult = Invoke-DevelopmentLifecycleSync `
    -Organization 'ai-enabled-ado-org' `
    -Project 'sample-project' `
    -Repository $repository `
    -DeploymentRunId 100 `
    -DeployedSha $deployedSha `
    -EvidenceWaitAttempts 1 `
    -EvidenceWaitSeconds 0 `
    -DryRun
Assert-True (
    $platformBoundaryResult.Action -eq 'Skipped'
) "The full API boundary must preserve a zero-AB platform/N/A skip."
Assert-True (
    $script:azureCalls -eq 0
) "A platform skip must not request Azure credentials or write to Boards."

$script:boundaryMode = 'zero-review'
Assert-Throws {
    Invoke-DevelopmentLifecycleSync `
        -Organization 'ai-enabled-ado-org' `
        -Project 'sample-project' `
        -Repository $repository `
        -DeploymentRunId 100 `
        -DeployedSha $deployedSha `
        -EvidenceWaitAttempts 1 `
        -EvidenceWaitSeconds 0 `
        -DryRun
} "no successful review workflow contains a Copilot review"
Assert-True (
    $script:azureCalls -eq 0
) "Missing reviews must fail before Azure credentials or Board writes."
$env:GITHUB_TOKEN = $originalGitHubToken

Write-Output "Development lifecycle synchronization tests passed."
