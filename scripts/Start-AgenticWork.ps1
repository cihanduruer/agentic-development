param(
    [string]$Organization = "ai-enabled-ado-org",
    [string]$Project = "sample-project",
    [string]$Repository = "cihanduruer/agentic-development",
    [string]$BaseBranch = "main",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

if (-not $env:COPILOT_AGENT_TOKEN -and -not $DryRun) {
    throw "COPILOT_AGENT_TOKEN is required."
}

$adoToken = az account get-access-token `
    --resource 499b84ac-1321-427f-aa17-267ca6975798 `
    --query accessToken `
    --output tsv
if (-not $adoToken) {
    throw "Could not obtain an Azure DevOps access token."
}

$adoHeaders = @{ Authorization = "Bearer $adoToken" }
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
$workItems = (Invoke-RestMethod `
    -Method Post `
    -Uri $queryUri `
    -Headers $adoHeaders `
    -ContentType "application/json" `
    -Body $wiql).workItems

foreach ($reference in $workItems) {
    $id = $reference.id
    $itemUri = "https://dev.azure.com/$Organization/$Project/_apis/wit/workitems/${id}?`$expand=relations&api-version=7.1"
    $item = Invoke-RestMethod -Uri $itemUri -Headers $adoHeaders
    $title = $item.fields."System.Title"
    $description = $item.fields."System.Description" -replace "<[^>]+>", " " -replace "\s+", " "
    $tags = [string]$item.fields."System.Tags"
    $workItemUrl = "https://dev.azure.com/$Organization/$Project/_workitems/edit/$id"

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

    $githubHeaders = @{
        Authorization = "Bearer $env:COPILOT_AGENT_TOKEN"
        Accept = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }
    $issueRequest = @{
        title = "[AB#$id] $title"
        body = $issueBody
        labels = @("azure-boards", "agentic-development")
        assignees = @("copilot-swe-agent[bot]")
        agent_assignment = @{
            target_repo = $Repository
            base_branch = $BaseBranch
            custom_instructions = "Implement the linked Azure Boards hotel-booking requirement. Follow AGENTS.md, cite canonical knowledge, run tests, and create a draft pull request."
            custom_agent = "hotel-developer"
            model = ""
        }
    } | ConvertTo-Json -Depth 8

    $issue = Invoke-RestMethod `
        -Method Post `
        -Uri "https://api.github.com/repos/$Repository/issues" `
        -Headers $githubHeaders `
        -ContentType "application/json" `
        -Body $issueRequest

    if (-not ($issue.assignees.login -contains "copilot-swe-agent")) {
        throw "GitHub issue $($issue.number) was created but Copilot was not assigned."
    }

    $updatedTags = (($tags -split ";" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) + "github-synced") |
        Select-Object -Unique
    $patch = @(
        @{ op = "test"; path = "/rev"; value = $item.rev }
        @{ op = "add"; path = "/fields/System.State"; value = "Active" }
        @{ op = "add"; path = "/fields/System.Tags"; value = ($updatedTags -join "; ") }
        @{ op = "add"; path = "/fields/System.History"; value = "Copilot coding agent started in GitHub issue #$($issue.number): $($issue.html_url)" }
        @{
            op = "add"
            path = "/relations/-"
            value = @{
                rel = "Hyperlink"
                url = $issue.html_url
                attributes = @{ comment = "GitHub Copilot development task" }
            }
        }
    ) | ConvertTo-Json -Depth 8

    Invoke-RestMethod `
        -Method Patch `
        -Uri "https://dev.azure.com/$Organization/$Project/_apis/wit/workitems/${id}?api-version=7.1" `
        -Headers $adoHeaders `
        -ContentType "application/json-patch+json" `
        -Body $patch | Out-Null

    [PSCustomObject]@{
        WorkItemId = $id
        GitHubIssue = $issue.number
        CopilotAssigned = $true
        Url = $issue.html_url
    }
}
