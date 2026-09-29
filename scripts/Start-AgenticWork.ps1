param(
    [string]$Organization = "ai-enabled-ado-org",
    [string]$Project = "sample-project",
    [string]$Repository = "cihanduruer/agentic-development",
    [string]$BaseBranch = "main",
    [int[]]$WorkItemId,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$script:CopilotDatabaseIds = @(198982749)
$script:CopilotNodeIds = @("BOT_kgDOC9w8XQ")
$script:CopilotLogins = @("copilot", "copilot-swe-agent", "copilot-swe-agent[bot]")

function Test-CopilotAssignee {
    param([object[]]$Assignees)

    foreach ($assignee in @($Assignees)) {
        $databaseId = if ($null -ne $assignee.databaseId) { $assignee.databaseId } else { $assignee.id }
        $nodeId = if ($null -ne $assignee.nodeId) { $assignee.nodeId } else { $assignee.node_id }
        $login = [string]$assignee.login

        if (($databaseId -as [long]) -in $script:CopilotDatabaseIds) {
            return $true
        }

        if ($nodeId -in $script:CopilotNodeIds) {
            return $true
        }

        if ($login.ToLowerInvariant() -in $script:CopilotLogins) {
            return $true
        }
    }

    return $false
}

function Test-WorkItemIssue {
    param(
        [Parameter(Mandatory)]
        [object]$Issue,

        [Parameter(Mandatory)]
        [int]$Id
    )

    $titlePattern = "^\[AB#$Id\](?:\s|$)"
    $bodyPattern = "(?m)^\s*Azure Boards work item:\s*\[AB#$Id\]\("
    return ([string]$Issue.title -match $titlePattern) -or ([string]$Issue.body -match $bodyPattern)
}

function Select-WorkItemIssue {
    param(
        [Parameter(Mandatory)]
        [object[]]$Issues,

        [Parameter(Mandatory)]
        [int]$Id
    )

    $matches = @($Issues | Where-Object {
        $null -eq $_.pull_request -and (Test-WorkItemIssue -Issue $_ -Id $Id)
    })

    if ($matches.Count -gt 1) {
        $numbers = ($matches.number | Sort-Object) -join ", "
        throw "Multiple GitHub issues link AB#$Id ($numbers); refusing to choose an idempotency target."
    }

    return $matches | Select-Object -First 1
}

function Invoke-GitHubRest {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Get", "Post", "Patch")]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Uri,

        [object]$Body
    )

    $headers = @{
        Authorization = "Bearer $env:COPILOT_AGENT_TOKEN"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $headers
    }

    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 10
    }

    Invoke-RestMethod @parameters
}

function Get-RepositoryIssues {
    param([Parameter(Mandatory)][string]$Repository)

    $issues = [System.Collections.Generic.List[object]]::new()
    $page = 1

    do {
        $batch = @(Invoke-GitHubRest -Method Get -Uri "https://api.github.com/repos/$Repository/issues?state=all&per_page=100&page=$page")
        foreach ($issue in $batch) {
            $issues.Add($issue)
        }
        $page++
    } while ($batch.Count -eq 100)

    return $issues.ToArray()
}

function Get-AdoWorkItems {
    param(
        [Parameter(Mandatory)]
        [string]$Organization,

        [Parameter(Mandatory)]
        [string]$Project,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [int[]]$WorkItemId
    )

    if ($WorkItemId.Count -gt 0) {
        return @($WorkItemId | Sort-Object -Unique)
    }

    $wiql = @{
        query = @"
SELECT [System.Id]
FROM WorkItems
WHERE [System.TeamProject] = '$Project'
  AND [System.WorkItemType] IN ('User Story', 'Bug')
  AND [System.State] = 'New'
  AND [System.Tags] NOT CONTAINS 'github-synced'
ORDER BY [Microsoft.VSTS.Common.Priority], [System.CreatedDate]
"@
    } | ConvertTo-Json

    $queryUri = "https://dev.azure.com/$Organization/$Project/_apis/wit/wiql?api-version=7.1"
    $result = Invoke-RestMethod `
        -Method Post `
        -Uri $queryUri `
        -Headers $Headers `
        -ContentType "application/json" `
        -Body $wiql
    return @($result.workItems.id)
}

function Invoke-AgenticIntake {
    param(
        [string]$Organization,
        [string]$Project,
        [string]$Repository,
        [string]$BaseBranch,
        [int[]]$WorkItemId,
        [switch]$DryRun
    )

    if (-not $env:COPILOT_AGENT_TOKEN -and -not $DryRun) {
        throw "COPILOT_AGENT_TOKEN is required and must be a GitHub user token."
    }

    if (-not $DryRun) {
        $tokenIdentity = Invoke-GitHubRest -Method Get -Uri "https://api.github.com/user"
        if ($tokenIdentity.type -ne "User") {
            throw "COPILOT_AGENT_TOKEN must authenticate a GitHub user; token identity type was '$($tokenIdentity.type)'."
        }
    }

    $adoToken = az account get-access-token `
        --resource 499b84ac-1321-427f-aa17-267ca6975798 `
        --query accessToken `
        --output tsv
    if (-not $adoToken) {
        throw "Could not obtain an Azure DevOps access token."
    }

    $adoHeaders = @{ Authorization = "Bearer $adoToken" }
    $ids = Get-AdoWorkItems `
        -Organization $Organization `
        -Project $Project `
        -Headers $adoHeaders `
        -WorkItemId $WorkItemId

    if ($ids.Count -eq 0) {
        Write-Output "No eligible Azure Boards work items found."
        return
    }

    $repositoryIssues = if ($DryRun) { @() } else { @(Get-RepositoryIssues -Repository $Repository) }

    foreach ($id in $ids) {
        $itemUri = "https://dev.azure.com/$Organization/$Project/_apis/wit/workitems/${id}?`$expand=relations&api-version=7.1"
        $item = Invoke-RestMethod -Uri $itemUri -Headers $adoHeaders
        $title = $item.fields."System.Title"
        $description = $item.fields."System.Description" -replace "<[^>]+>", " " -replace "\s+", " "
        $tags = @([string]$item.fields."System.Tags" -split ";" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        $state = [string]$item.fields."System.State"
        $type = [string]$item.fields."System.WorkItemType"
        $teamProject = [string]$item.fields."System.TeamProject"
        $workItemUrl = "https://dev.azure.com/$Organization/$Project/_workitems/edit/$id"

        if ($teamProject -ne $Project -or $type -notin @("User Story", "Bug")) {
            throw "AB#$id is not an eligible User Story or Bug in project '$Project'."
        }

        if ($state -notin @("New", "Active") -and "github-synced" -notin $tags) {
            throw "AB#$id is '$state'; only New or Active unsynchronized items can be started."
        }

        $issueBody = @"
Azure Boards work item: [AB#$id]($workItemUrl)

## Product requirement

$description

## Agent operating contract

- Read `AGENTS.md` and the relevant files under `docs/knowledge/` before changing code.
- Record the knowledge revision used.
- Implement only this hotel-booking requirement.
- Add or update automated tests for acceptance criteria and edge cases.
- Run formatting, build, tests, and Bicep validation when relevant.
- Update canonical knowledge when behavior changes.
- Do not change platform automation unless this requirement explicitly needs it.
- Open a pull request containing `AB#$id` in its title or body.
"@

        if ($DryRun) {
            [PSCustomObject]@{ WorkItemId = $id; Title = $title; Action = "Would start Copilot" }
            continue
        }

        $issue = Select-WorkItemIssue -Issues $repositoryIssues -Id $id
        $assignment = @{
            target_repo = $Repository
            base_branch = $BaseBranch
            custom_instructions = "Implement the linked Azure Boards hotel-booking requirement. Follow AGENTS.md, cite canonical knowledge, run tests, and create a draft pull request."
            custom_agent = "hotel-developer"
            model = ""
        }

        if ($null -eq $issue) {
            $issue = Invoke-GitHubRest `
                -Method Post `
                -Uri "https://api.github.com/repos/$Repository/issues" `
                -Body @{
                    title = "[AB#$id] $title"
                    body = $issueBody
                    labels = @("azure-boards", "agentic-development")
                    assignees = @("copilot-swe-agent[bot]")
                    agent_assignment = $assignment
                }
            $repositoryIssues += $issue
        }
        elseif (-not (Test-CopilotAssignee -Assignees $issue.assignees)) {
            $issue = Invoke-GitHubRest `
                -Method Post `
                -Uri "https://api.github.com/repos/$Repository/issues/$($issue.number)/assignees" `
                -Body @{
                    assignees = @("copilot-swe-agent[bot]")
                    agent_assignment = $assignment
                }
        }

        if (-not (Test-CopilotAssignee -Assignees $issue.assignees)) {
            $verifiedIssue = Invoke-GitHubRest `
                -Method Get `
                -Uri "https://api.github.com/repos/$Repository/issues/$($issue.number)"
            if (-not (Test-CopilotAssignee -Assignees $verifiedIssue.assignees)) {
                throw "GitHub issue $($issue.number) exists, but the API response does not identify a supported Copilot assignee."
            }
            $issue = $verifiedIssue
        }

        $issueUrl = [string]$issue.html_url
        $hasLink = @($item.relations | Where-Object {
            $_.rel -eq "Hyperlink" -and $_.url -eq $issueUrl
        }).Count -gt 0
        $patch = [System.Collections.Generic.List[object]]::new()
        $patch.Add(@{ op = "test"; path = "/rev"; value = $item.rev })

        if ($state -eq "New") {
            $patch.Add(@{ op = "add"; path = "/fields/System.State"; value = "Active" })
        }

        if ("github-synced" -notin $tags) {
            $updatedTags = @($tags + "github-synced" | Select-Object -Unique)
            $patch.Add(@{ op = "add"; path = "/fields/System.Tags"; value = ($updatedTags -join "; ") })
        }

        if (-not $hasLink) {
            $patch.Add(@{
                op = "add"
                path = "/relations/-"
                value = @{
                    rel = "Hyperlink"
                    url = $issueUrl
                    attributes = @{ comment = "GitHub Copilot development task" }
                }
            })
        }

        if ($patch.Count -gt 1) {
            $patch.Add(@{
                op = "add"
                path = "/fields/System.History"
                value = "Copilot coding agent started in GitHub issue #$($issue.number): $issueUrl"
            })
            Invoke-RestMethod `
                -Method Patch `
                -Uri "https://dev.azure.com/$Organization/$Project/_apis/wit/workitems/${id}?api-version=7.1" `
                -Headers $adoHeaders `
                -ContentType "application/json-patch+json" `
                -Body ($patch | ConvertTo-Json -Depth 10) | Out-Null
        }

        [PSCustomObject]@{
            WorkItemId = $id
            GitHubIssue = $issue.number
            CopilotAssigned = $true
            BoardUpdated = $patch.Count -gt 1
            Url = $issueUrl
        }
    }
}

if ($MyInvocation.InvocationName -ne ".") {
    Invoke-AgenticIntake `
        -Organization $Organization `
        -Project $Project `
        -Repository $Repository `
        -BaseBranch $BaseBranch `
        -WorkItemId $WorkItemId `
        -DryRun:$DryRun
}
