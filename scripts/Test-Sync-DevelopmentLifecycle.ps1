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
$headSha = "b" * 40
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
    body = "Evidence for AB#959."
    merged_at = "2026-09-29T08:00:00Z"
    merge_commit_sha = $deployedSha
    html_url = "https://github.com/$repository/pull/6"
    base = [pscustomobject]@{
        ref = "main"
        repo = [pscustomobject]@{ full_name = $repository }
    }
    head = [pscustomobject]@{
        sha = $headSha
        repo = [pscustomobject]@{ full_name = $repository }
    }
}
$issue = [pscustomobject]@{
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
    artifacts = @([pscustomobject]@{ name = "qa-evidence-$deployedSha"; expired = $false })
}
$reviewRun = [pscustomobject]@{
    conclusion = "success"
    path = ".github/workflows/hotel-code-review.yml"
    created_at = "2026-09-29T07:59:00Z"
    updated_at = "2026-09-29T08:00:00Z"
    html_url = "https://github.com/$repository/actions/runs/103"
    pull_requests = @([pscustomobject]@{ number = 6 })
}
$review = [pscustomobject]@{
    user = [pscustomobject]@{ login = "copilot-pull-request-reviewer[bot]" }
    commit_id = $headSha
    submitted_at = "2026-09-29T07:59:30Z"
}
$arguments = @{
    ExpectedSha = $deployedSha
    Deployment = $deployment
    PullRequests = @($pull)
    Issues = @($issue)
    ValidationRuns = @($validation)
    QaRuns = @($qa)
    ReviewRuns = @($reviewRun)
    Reviews = @($review)
    Organization = "ai-enabled-ado-org"
    Project = "sample-project"
    Repository = $repository
}

$evidence = Resolve-LifecycleEvidence @arguments
Assert-True ($evidence.WorkItemId -eq 959) "The trusted AB#959 evidence should resolve."

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
        "System.Tags" = "github-synced; ready-for-triage"
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
                name = "qa-evidence-$deployedSha"
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
$targetedQa = @(Get-QaEvidenceRuns -Repository $repository -ExpectedSha $deployedSha)
Assert-True ($targetedQa.Count -eq 1) "Exact-name QA artifact lookup should return its workflow run."
Assert-True ($script:pageRequestCount -eq 2) "QA lookup should use one artifact and one run request."

Write-Output "Development lifecycle synchronization tests passed."
