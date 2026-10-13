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
$script:requestedIssueMethod = $null
$script:requestedIssueBody = $null
function Invoke-GitHubRest {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body
    )

    $script:requestedIssueMethod = $Method
    $script:requestedIssueUri = $Uri
    if ($Method -eq "Patch") {
        $script:requestedIssueBody = $Body.body
        $updatedIssue = $linkedIssue.PSObject.Copy()
        $updatedIssue.body = $Body.body
        return $updatedIssue
    }

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

$bodyWithCriteria = New-WorkItemIssueBody `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959 `
    -Description "<p>Guests can filter available rooms.</p>" `
    -AcceptanceCriteria "<ul><li>Only rooms matching selected amenities appear.</li></ul>"
Assert-True `
    -Condition ($bodyWithCriteria -match "(?s)## Product requirement\s+Guests can filter available rooms\.\s+## Acceptance criteria\s+Only rooms matching selected amenities appear\.") `
    -Message "Description and acceptance criteria should be rendered as separate readable sections."

$bodyWithoutCriteria = New-WorkItemIssueBody `
    -Organization "ai-enabled-ado-org" `
    -Project "sample-project" `
    -Id 959 `
    -Description "<p>Guests can browse rooms.</p>" `
    -AcceptanceCriteria $null
Assert-True `
    -Condition ($bodyWithoutCriteria -match "(?s)## Product requirement\s+Guests can browse rooms\.\s+## Acceptance criteria\s+Not provided\.") `
    -Message "Missing acceptance criteria should be represented clearly without losing the description."

$staleIssue = $linkedIssue.PSObject.Copy()
$staleIssue.body = "Previous intake body"
$refreshedIssue = Sync-WorkItemIssueBody `
    -Repository "cihanduruer/agentic-development" `
    -Issue $staleIssue `
    -Body $bodyWithCriteria
Assert-True `
    -Condition ($script:requestedIssueMethod -eq "Patch") `
    -Message "Recovery should patch the existing canonical issue rather than create another issue."
Assert-True `
    -Condition ($script:requestedIssueUri -eq "https://api.github.com/repos/cihanduruer/agentic-development/issues/5") `
    -Message "Recovery should refresh the selected canonical issue."
Assert-True `
    -Condition ($script:requestedIssueBody -eq $bodyWithCriteria -and $refreshedIssue.number -eq 5) `
    -Message "The canonical issue body should be refreshed in place."

$script:requestedIssueMethod = $null
$unchangedIssue = Sync-WorkItemIssueBody `
    -Repository "cihanduruer/agentic-development" `
    -Issue $refreshedIssue `
    -Body $bodyWithCriteria
Assert-True `
    -Condition ($null -eq $script:requestedIssueMethod -and $unchangedIssue.number -eq 5) `
    -Message "An already-current canonical issue body should not require another update."

Write-Output "Start-AgenticWork tests passed."
