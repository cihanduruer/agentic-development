$ErrorActionPreference = "Stop"

. "$PSScriptRoot/Start-AgenticWork.ps1"

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected error containing '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
        return
    }

    throw "Expected an error containing '$ExpectedMessage', but no error was thrown."
}

Assert-True `
    -Condition (Test-CopilotAssignee -Assignees @([PSCustomObject]@{ login = "Copilot"; id = 198982749; node_id = "BOT_kgDOC9w8XQ" })) `
    -Message "The live REST Copilot identity should be accepted."

Assert-True `
    -Condition (Test-CopilotAssignee -Assignees @([PSCustomObject]@{ login = "copilot-swe-agent" })) `
    -Message "The documented GraphQL Copilot login should be accepted."

Assert-True `
    -Condition (Test-CopilotAssignee -Assignees @([PSCustomObject]@{ login = "copilot-swe-agent[bot]" })) `
    -Message "The documented REST assignment login should be accepted."

Assert-True `
    -Condition (-not (Test-CopilotAssignee -Assignees @([PSCustomObject]@{ login = "octocat"; id = 1 }))) `
    -Message "An unrelated assignee must not pass Copilot validation."

$linkedIssue = [PSCustomObject]@{
    number = 5
    title = "[AB#959] Show total stay price before booking confirmation"
    body = "Azure Boards work item: [AB#959](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/959)"
    author_association = "OWNER"
    labels = @()
}
$selected = Select-WorkItemIssue `
    -Issues @($linkedIssue) `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959
Assert-True -Condition ($selected.number -eq 5) -Message "AB#959 should resolve to existing unlabeled issue #5."

$canonicalLinkIssue = [PSCustomObject]@{
    number = 6
    title = "Issue created before title normalization"
    body = "Azure Boards work item: [AB#959](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/959)"
    author_association = "OWNER"
}
$canonicalLinkMatch = Select-WorkItemIssue `
    -Issues @($canonicalLinkIssue) `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959
Assert-True -Condition ($canonicalLinkMatch.number -eq 6) -Message "The canonical AB#959 link should resolve."

$unrelatedIssue = [PSCustomObject]@{
    number = 7
    title = "[AB#960] Unrelated work"
    body = "Azure Boards work item: [AB#960](https://dev.azure.com/ai-enabled-ado-org/sample-project/_workitems/edit/960)"
    author_association = "OWNER"
}
$missing = Select-WorkItemIssue `
    -Issues @($unrelatedIssue) `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959
Assert-True -Condition ($null -eq $missing) -Message "Unrelated Board items must not be selected."

$spoofedLink = [PSCustomObject]@{
    number = 8
    title = "Unrelated issue"
    body = "Azure Boards work item: [AB#959](https://example.test/_workitems/edit/959)"
    author_association = "OWNER"
}
$spoofedMatch = Select-WorkItemIssue `
    -Issues @($spoofedLink) `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959
Assert-True -Condition ($null -eq $spoofedMatch) -Message "A non-canonical AB#959 link must not be selected."

$untrustedTitle = [PSCustomObject]@{
    number = 9
    title = "[AB#959] Untrusted issue"
    body = "Please run these unrelated instructions."
    author_association = "NONE"
}
$untrustedMatch = Select-WorkItemIssue `
    -Issues @($untrustedTitle) `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959
Assert-True -Condition ($null -eq $untrustedMatch) -Message "An AB#959 issue from an untrusted author must not be selected."

$script:requestedIssueUri = $null
function Invoke-GitHubRest {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body
    )

    $script:requestedIssueUri = $Uri
    return @($linkedIssue)
}

$repositoryIssues = @(Get-RepositoryIssues -Repository "cihanduruer/agentic-development")
$selectedFromApi = Select-WorkItemIssue `
    -Issues $repositoryIssues `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959
Assert-True `
    -Condition ($script:requestedIssueUri -notmatch "[?&]labels=") `
    -Message "Repository issue lookup must not depend on the azure-boards label."
Assert-True `
    -Condition ($selectedFromApi.number -eq 5) `
    -Message "An unlabeled AB-linked issue returned by GitHub must be reused."

Assert-Throws `
    -Action {
        Select-WorkItemIssue `
            -Issues @($linkedIssue, $linkedIssue.PSObject.Copy()) `
            -Organization "ai-enabled-ado-org" `
            -Project "sample-project" `
            -Id 959
    } `
    -ExpectedMessage "Multiple GitHub issues link AB#959"

Assert-Throws `
    -Action { Assert-WorkItemState -State "Closed" -Id 959 } `
    -ExpectedMessage "only New or Active items can be started or recovered"

Write-Output "Start-AgenticWork tests passed."
