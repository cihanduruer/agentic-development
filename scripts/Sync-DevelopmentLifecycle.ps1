[CmdletBinding()]
param(
    [string]$Organization = "ai-enabled-ado-org",
    [string]$Project = "sample-project",
    [string]$Repository = "cihanduruer/agentic-development",
    [Parameter(Mandatory)]
    [long]$DeploymentRunId,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$DeployedSha,
    [int]$PullRequestNumber,
    [ValidateRange(1, 60)]
    [int]$EvidenceWaitAttempts = 1,
    [ValidateRange(0, 300)]
    [int]$EvidenceWaitSeconds = 0,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$script:TrustedAssociations = @("OWNER", "MEMBER", "COLLABORATOR")
$script:CopilotReviewer = "copilot-pull-request-reviewer[bot]"

. "$PSScriptRoot/PullRequestClassification.ps1"

function Get-PullRequestAbIds {
    param(
        [AllowEmptyString()][string]$Title,
        [AllowEmptyString()][string]$Body
    )

    return @(
        (Get-PullRequestClassification -Title $Title -Body $Body).WorkItemIds
    )
}

function Test-CanonicalWorkItemIssue {
    param(
        [Parameter(Mandatory)][object]$Issue,
        [Parameter(Mandatory)][string]$Organization,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][int]$Id
    )

    $titlePattern = "^\[AB#$Id\](?:\s|$)"
    $workItemUrl = [regex]::Escape(
        "https://dev.azure.com/$Organization/$Project/_workitems/edit/$Id")
    $bodyPattern = "(?m)^\s*Azure Boards work item:\s*\[AB#$Id\]\($workItemUrl\)\s*$"

    return $Issue.author_association -in $script:TrustedAssociations -and (
        [string]$Issue.title -match $titlePattern -or
        [string]$Issue.body -match $bodyPattern)
}

function Resolve-LifecycleEvidence {
    param(
        [Parameter(Mandatory)][string]$ExpectedSha,
        [Parameter(Mandatory)][object]$Deployment,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$PullRequests,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Issues,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ValidationRuns,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$QaRuns,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ReviewRuns,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Reviews,
        [Parameter(Mandatory)][string]$Organization,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Repository,
        [int]$ExpectedPullRequestNumber
    )

    if ($Deployment.conclusion -ne "success" -or
        $Deployment.path -ne ".github/workflows/deploy-development.yml" -or
        $Deployment.head_branch -ne "main" -or
        $Deployment.head_sha -ne $ExpectedSha -or
        -not $Deployment.on_main) {
        throw "Development deployment does not prove successful deployment of exact main SHA '$ExpectedSha'."
    }

    $pullMatches = @($PullRequests | Where-Object {
        $_.merged_at -and
        $_.merge_commit_sha -eq $ExpectedSha -and
        $_.base.ref -eq "main" -and
        $_.base.repo.full_name -eq $Repository -and
        $_.head.repo.full_name -eq $Repository
    })
    if ($pullMatches.Count -ne 1) {
        throw "Expected one merged same-repository pull request for deployed SHA '$ExpectedSha'; found $($pullMatches.Count)."
    }
    $pull = $pullMatches[0]
    if ($ExpectedPullRequestNumber -gt 0 -and $pull.number -ne $ExpectedPullRequestNumber) {
        throw "Deployed SHA '$ExpectedSha' belongs to PR #$($pull.number), not selected PR #$ExpectedPullRequestNumber."
    }

    $classification = Get-PullRequestClassification `
        -Title ([string]$pull.title) `
        -Body ([string]$pull.body)
    $ids = @($classification.WorkItemIds)
    if ($classification.IsPlatformOnly -and $classification.BoardsNotApplicable) {
        return [pscustomobject]@{
            Action = "Skipped"
            Reason = "Merged PR #$($pull.number) explicitly declares a platform change with Azure Boards not applicable."
            PullRequestNumber = [int]$pull.number
            DeployedSha = $ExpectedSha
        }
    }

    if ($classification.IsPlatformOnly) {
        return [pscustomobject]@{
            Action = "Skipped"
            Reason = "Merged PR #$($pull.number) has no Azure Boards identity."
            PullRequestNumber = [int]$pull.number
            DeployedSha = $ExpectedSha
        }
    }
    $workItemId = $ids[0]
    $metadataDigest = Get-PullRequestMetadataDigest `
        -Title ([string]$pull.title) `
        -Body ([string]$pull.body)

    $issueMatches = @($Issues | Where-Object {
        $null -eq $_.pull_request -and
        (Test-CanonicalWorkItemIssue `
            -Issue $_ `
            -Organization $Organization `
            -Project $Project `
            -Id $workItemId)
    })
    if ($issueMatches.Count -ne 1) {
        throw "AB#$workItemId must resolve to exactly one trusted canonical GitHub issue; found $($issueMatches.Count)."
    }

    $validation = @($ValidationRuns | Where-Object {
        $_.conclusion -eq "success" -and
        $_.path -eq ".github/workflows/pr-validation.yml" -and
        $_.event -eq "push" -and
        $_.head_branch -eq "main" -and
        $_.head_sha -eq $ExpectedSha
    } | Sort-Object created_at -Descending | Select-Object -First 1)
    if ($validation.Count -ne 1) {
        throw "Evidence pending: no successful PR validation exists for exact deployed SHA '$ExpectedSha'."
    }

    $qa = @($QaRuns | Where-Object {
        $_.conclusion -eq "success" -and
        $_.path -eq ".github/workflows/qa-evidence.yml" -and
        @($_.artifacts | Where-Object {
            $_.name -eq "qa-evidence-$ExpectedSha-$metadataDigest" -and -not $_.expired
        }).Count -gt 0
    } | Sort-Object created_at -Descending | Select-Object -First 1)
    if ($qa.Count -ne 1) {
        throw "Evidence pending: no successful unexpired QA evidence exists for exact deployed SHA '$ExpectedSha'."
    }

    $eligibleReviewRuns = @($ReviewRuns | Where-Object {
        $_.conclusion -eq "success" -and
        $_.path -eq ".github/workflows/hotel-code-review.yml" -and
        @($_.pull_requests | Where-Object { $_.number -eq $pull.number }).Count -gt 0
    })
    $exactHeadReviews = @($Reviews | Where-Object {
        $_.user.login -eq $script:CopilotReviewer -and
        $_.commit_id -eq $pull.head.sha
    })
    $reviewRun = @($eligibleReviewRuns | Where-Object {
        $run = $_
        @($exactHeadReviews | Where-Object {
            [DateTimeOffset]$run.created_at -le [DateTimeOffset]$_.submitted_at -and
            [DateTimeOffset]$run.updated_at -ge [DateTimeOffset]$_.submitted_at
        }).Count -gt 0
    } | Sort-Object created_at -Descending | Select-Object -First 1)
    if ($reviewRun.Count -ne 1) {
        throw "Evidence pending: no successful review workflow contains a Copilot review for exact pull request head '$($pull.head.sha)'."
    }

    return [pscustomobject]@{
        WorkItemId = $workItemId
        PullRequestNumber = [int]$pull.number
        PullRequestUrl = [string]$pull.html_url
        DeploymentUrl = [string]$Deployment.html_url
        ValidationUrl = [string]$validation[0].html_url
        QaUrl = [string]$qa[0].html_url
        ReviewUrl = [string]$reviewRun[0].html_url
        DeployedSha = $ExpectedSha
    }
}

function New-WorkItemEvidencePatch {
    param(
        [Parameter(Mandatory)][object]$WorkItem,
        [Parameter(Mandatory)][object]$Evidence
    )

    $tags = @(
        [string]$WorkItem.fields."System.Tags" -split ";" |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ }
    )
    $tagSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $updatedTags = [Collections.Generic.List[string]]::new()
    foreach ($tag in $tags) {
        if ($tag -ne "ready-for-triage" -and $tagSet.Add($tag)) {
            $updatedTags.Add($tag)
        }
    }
    if ($tagSet.Add("delivery-evidence")) {
        $updatedTags.Add("delivery-evidence")
    }

    $existingUrls = @($WorkItem.relations | Where-Object {
        $_.rel -eq "Hyperlink"
    } | ForEach-Object { [string]$_.url })
    $links = @(
        [pscustomobject]@{ Url = $Evidence.PullRequestUrl; Label = "Merged GitHub pull request" }
        [pscustomobject]@{ Url = $Evidence.DeploymentUrl; Label = "Successful development deployment" }
        [pscustomobject]@{ Url = $Evidence.ValidationUrl; Label = "Exact-SHA PR validation evidence" }
        [pscustomobject]@{ Url = $Evidence.QaUrl; Label = "Exact-SHA QA evidence" }
        [pscustomobject]@{ Url = $Evidence.ReviewUrl; Label = "Exact-head Copilot review evidence" }
    )

    $changes = [Collections.Generic.List[object]]::new()
    if (($tags -join "; ") -ne ($updatedTags -join "; ")) {
        $changes.Add(@{
            op = "add"
            path = "/fields/System.Tags"
            value = ($updatedTags -join "; ")
        })
    }
    foreach ($link in $links) {
        if ($link.Url -notin $existingUrls) {
            $changes.Add(@{
                op = "add"
                path = "/relations/-"
                value = @{
                    rel = "Hyperlink"
                    url = $link.Url
                    attributes = @{ comment = $link.Label }
                }
            })
        }
    }

    if ($changes.Count -eq 0) {
        return @()
    }

    $patch = [Collections.Generic.List[object]]::new()
    $patch.Add(@{ op = "test"; path = "/rev"; value = $WorkItem.rev })
    foreach ($change in $changes) {
        $patch.Add($change)
    }
    $patch.Add(@{
        op = "add"
        path = "/fields/System.History"
        value = "Development delivery evidence recorded for commit $($Evidence.DeployedSha) from PR #$($Evidence.PullRequestNumber). Product acceptance state was preserved."
    })
    return $patch.ToArray()
}

function Assert-EligibleWorkItem {
    param(
        [Parameter(Mandatory)][object]$WorkItem,
        [Parameter(Mandatory)][string]$Project
    )

    $teamProject = [string]$WorkItem.fields."System.TeamProject"
    $type = [string]$WorkItem.fields."System.WorkItemType"
    if ($teamProject -ne $Project -or $type -notin @("User Story", "Bug")) {
        throw "Azure Boards item is not an eligible User Story or Bug in project '$Project'."
    }
}

function Assert-WorkItemUpdateStatus {
    param([Parameter(Mandatory)][int]$StatusCode)

    if ($StatusCode -in @(409, 412)) {
        throw "Azure Boards revision conflict; no lifecycle evidence was written."
    }
    throw "Azure Boards update failed with HTTP $StatusCode."
}

function Test-RetryableEvidenceError {
    param([Parameter(Mandatory)][string]$Message)
    return $Message -like "Evidence pending:*"
}

function Invoke-GitHubApi {
    param([Parameter(Mandatory)][string]$Uri)

    $headers = @{
        Authorization = ("Bearer {0}" -f $env:GITHUB_TOKEN)
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    return Invoke-RestMethod -Uri $Uri -Headers $headers
}

function Get-GitHubPages {
    param([Parameter(Mandatory)][string]$Uri)

    $results = [Collections.Generic.List[object]]::new()
    $page = 1
    do {
        $separator = if ($Uri.Contains("?")) { "&" } else { "?" }
        $response = Invoke-GitHubApi -Uri "$Uri${separator}per_page=100&page=$page"
        $batch = @($response | ForEach-Object { $_ })
        foreach ($item in $batch) {
            $results.Add($item)
        }
        $page++
    } while ($batch.Count -eq 100)
    return $results.ToArray()
}

function Get-WorkflowRuns {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Workflow,
        [string]$HeadSha
    )

    $runs = [Collections.Generic.List[object]]::new()
    for ($page = 1; $page -le 10; $page++) {
        $headFilter = if ($HeadSha) { "&head_sha=$HeadSha" } else { "" }
        $response = Invoke-GitHubApi -Uri (
            "https://api.github.com/repos/$Repository/actions/workflows/$Workflow/runs?status=completed&per_page=100&page=$page$headFilter")
        $batch = @($response.workflow_runs | ForEach-Object { $_ })
        foreach ($run in $batch) {
            $runs.Add($run)
        }
        if ($batch.Count -lt 100) {
            break
        }
    }
    return $runs.ToArray()
}

function Get-QaEvidenceRuns {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$ExpectedSha,
        [Parameter(Mandatory)][string]$MetadataDigest
    )

    $artifactName = "qa-evidence-$ExpectedSha-$MetadataDigest"
    $response = Invoke-GitHubApi -Uri (
        "https://api.github.com/repos/$Repository/actions/artifacts?name=$artifactName&per_page=100")
    $artifacts = @($response.artifacts | Where-Object {
        $_.name -eq $artifactName -and -not $_.expired
    })
    $runs = [Collections.Generic.List[object]]::new()
    foreach ($group in ($artifacts | Group-Object { $_.workflow_run.id })) {
        $run = Invoke-GitHubApi -Uri (
            "https://api.github.com/repos/$Repository/actions/runs/$($group.Name)")
        $run | Add-Member -NotePropertyName artifacts -NotePropertyValue @($group.Group) -Force
        $runs.Add($run)
    }
    return $runs.ToArray()
}

function Get-CanonicalIssueCandidates {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$Ids
    )

    $issues = [Collections.Generic.List[object]]::new()
    foreach ($id in $Ids) {
        $query = [uri]::EscapeDataString("repo:$Repository is:issue `"AB#$id`"")
        $expectedCount = $null
        $receivedCount = 0
        for ($page = 1; $page -le 10; $page++) {
            $response = Invoke-GitHubApi -Uri (
                "https://api.github.com/search/issues?q=$query&per_page=100&page=$page")
            if ($response.incomplete_results -eq $true) {
                throw "GitHub issue search for AB#$id reported incomplete results."
            }

            $totalCount = [int]$response.total_count
            if ($totalCount -gt 1000) {
                throw "GitHub issue search for AB#$id exceeds the 1,000-result completeness limit."
            }
            if ($null -eq $expectedCount) {
                $expectedCount = $totalCount
            } elseif ($totalCount -ne $expectedCount) {
                throw "GitHub issue search for AB#$id changed while results were paged."
            }

            $batch = @($response.items)
            foreach ($issue in $batch) {
                $issues.Add($issue)
            }
            $receivedCount += $batch.Count
            if ($receivedCount -ge $expectedCount) {
                break
            }
            if ($batch.Count -eq 0) {
                throw "GitHub issue search for AB#$id returned truncated results."
            }
        }
        if ($receivedCount -lt $expectedCount) {
            throw "GitHub issue search for AB#$id did not return all $expectedCount results."
        }
    }
    return $issues.ToArray()
}

function Get-DeployedToMainComparisonUri {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$DeployedSha
    )
    return "https://api.github.com/repos/$Repository/compare/$DeployedSha...main"
}

function Test-CommitOnMain {
    param([Parameter(Mandatory)][string]$ComparisonStatus)
    return $ComparisonStatus -in @("identical", "ahead")
}

function Invoke-DevelopmentLifecycleSync {
    param(
        [string]$Organization,
        [string]$Project,
        [string]$Repository,
        [long]$DeploymentRunId,
        [string]$DeployedSha,
        [int]$PullRequestNumber,
        [int]$EvidenceWaitAttempts,
        [int]$EvidenceWaitSeconds,
        [switch]$DryRun
    )

    if (-not $env:GITHUB_TOKEN) {
        throw "GITHUB_TOKEN is required."
    }

    $deployment = Invoke-GitHubApi -Uri (
        "https://api.github.com/repos/$Repository/actions/runs/$DeploymentRunId")
    $comparison = Invoke-GitHubApi -Uri (
        Get-DeployedToMainComparisonUri `
            -Repository $Repository `
            -DeployedSha $DeployedSha)
    $deployment | Add-Member -NotePropertyName on_main -NotePropertyValue (
        Test-CommitOnMain -ComparisonStatus $comparison.status) -Force

    $pulls = @(Get-GitHubPages -Uri (
        "https://api.github.com/repos/$Repository/commits/$DeployedSha/pulls"))
    $candidatePulls = @($pulls | Where-Object { $_.merge_commit_sha -eq $DeployedSha })
    $candidateIds = @(
        if ($candidatePulls.Count -eq 1) {
            Get-PullRequestAbIds `
            -Title ([string]$candidatePulls[0].title) `
                -Body ([string]$candidatePulls[0].body)
        }
    )
    $candidateMetadataDigest = if ($candidatePulls.Count -eq 1) {
        Get-PullRequestMetadataDigest `
            -Title ([string]$candidatePulls[0].title) `
            -Body ([string]$candidatePulls[0].body)
    } else {
        ''
    }
    $issues = @(Get-CanonicalIssueCandidates `
        -Repository $Repository `
        -Ids $candidateIds)
    $evidence = $null
    for ($attempt = 1; $attempt -le $EvidenceWaitAttempts; $attempt++) {
        $validationRuns = @(Get-WorkflowRuns `
            -Repository $Repository `
            -Workflow "pr-validation.yml" `
            -HeadSha $DeployedSha)
        $qaRuns = @(Get-QaEvidenceRuns `
            -Repository $Repository `
            -ExpectedSha $DeployedSha `
            -MetadataDigest $candidateMetadataDigest)
        $reviewRuns = @(Get-WorkflowRuns -Repository $Repository -Workflow "hotel-code-review.yml")
        $reviews = @(
            if ($candidatePulls.Count -eq 1) {
                Get-GitHubPages -Uri (
                    "https://api.github.com/repos/$Repository/pulls/$($candidatePulls[0].number)/reviews")
            }
        )

        try {
            $evidence = Resolve-LifecycleEvidence `
                -ExpectedSha $DeployedSha `
                -Deployment $deployment `
                -PullRequests $pulls `
                -Issues $issues `
                -ValidationRuns $validationRuns `
                -QaRuns $qaRuns `
                -ReviewRuns $reviewRuns `
                -Reviews $reviews `
                -Organization $Organization `
                -Project $Project `
                -Repository $Repository `
                -ExpectedPullRequestNumber $PullRequestNumber
            break
        }
        catch {
            if (-not (Test-RetryableEvidenceError -Message $_.Exception.Message) -or
                $attempt -eq $EvidenceWaitAttempts) {
                throw
            }
            Write-Output "$($_.Exception.Message) Retry $attempt/$EvidenceWaitAttempts."
            Start-Sleep -Seconds $EvidenceWaitSeconds
        }
    }

    if ($evidence.Action -eq "Skipped") {
        return $evidence
    }

    $adoToken = az account get-access-token `
        --resource 499b84ac-1321-427f-aa17-267ca6975798 `
        --query accessToken `
        --output tsv
    if (-not $adoToken) {
        throw "Could not obtain an Azure DevOps access token."
    }
    $headers = @{ Authorization = ("Bearer {0}" -f $adoToken) }
    $itemUri = "https://dev.azure.com/$Organization/$Project/_apis/wit/workitems/$($evidence.WorkItemId)?`$expand=relations&api-version=7.1"
    $workItem = Invoke-RestMethod -Uri $itemUri -Headers $headers
    Assert-EligibleWorkItem -WorkItem $workItem -Project $Project
    $patch = @(New-WorkItemEvidencePatch -WorkItem $workItem -Evidence $evidence)

    if ($patch.Count -eq 0) {
        return [pscustomobject]@{
            WorkItemId = $evidence.WorkItemId
            DeployedSha = $DeployedSha
            Action = "NoOp"
            State = [string]$workItem.fields."System.State"
        }
    }
    if ($DryRun) {
        return [pscustomobject]@{
            WorkItemId = $evidence.WorkItemId
            DeployedSha = $DeployedSha
            Action = "WouldUpdate"
            State = [string]$workItem.fields."System.State"
            Operations = $patch.Count
        }
    }

    try {
        Invoke-RestMethod `
            -Method Patch `
            -Uri "https://dev.azure.com/$Organization/$Project/_apis/wit/workitems/$($evidence.WorkItemId)?api-version=7.1" `
            -Headers $headers `
            -ContentType "application/json-patch+json" `
            -Body ($patch | ConvertTo-Json -Depth 10) | Out-Null
    }
    catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        Assert-WorkItemUpdateStatus -StatusCode $statusCode
    }

    return [pscustomobject]@{
        WorkItemId = $evidence.WorkItemId
        DeployedSha = $DeployedSha
        Action = "Updated"
        State = [string]$workItem.fields."System.State"
    }
}

if ($MyInvocation.InvocationName -ne ".") {
    Invoke-DevelopmentLifecycleSync `
        -Organization $Organization `
        -Project $Project `
        -Repository $Repository `
        -DeploymentRunId $DeploymentRunId `
        -DeployedSha $DeployedSha `
        -PullRequestNumber $PullRequestNumber `
        -EvidenceWaitAttempts $EvidenceWaitAttempts `
        -EvidenceWaitSeconds $EvidenceWaitSeconds `
        -DryRun:$DryRun
}
