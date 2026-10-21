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
    -AcceptanceCriteria "<div><h3>Choose amenities</h3><p>Filter uses pool &amp; spa.</p><ul><li>Only rooms with the pool.</li><li>Only rooms with the spa.</li></ul><br/>Review selected amenities.</div>"
Assert-True `
    -Condition ($bodyWithCriteria -match "(?s)## Product requirement\s+Guests can filter available rooms\.\s+## Acceptance criteria\s+Choose amenities\s+Filter uses pool & spa\.\s+- Only rooms with the pool\.\s+- Only rooms with the spa\.\s+Review selected amenities\.\s+## Agent operating contract") `
    -Message "Description and decoded acceptance criteria should render as separate readable sections."
Assert-True `
    -Condition ($bodyWithCriteria -match '(?m)^- Only rooms with the pool\.$' -and $bodyWithCriteria -match '(?m)^- Only rooms with the spa\.$') `
    -Message "Each acceptance-criteria list item should remain on a separate line."

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

. "$PSScriptRoot/Start-AgenticWork.ps1"

function az {
    return "mock-ado-token"
}

function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $ContentType, $Body)

    if ($Uri -eq "https://api.github.com/user") {
        return [PSCustomObject]@{ type = "User" }
    }

    if ($Uri -match "^https://api\.github\.com/repos/cihanduruer/agentic-development/issues\?") {
        return $script:intakeIssues
    }

    if ($Uri -like "https://dev.azure.com/*") {
        if ($Method -eq "Patch") {
            $script:boardPatches.Add(($Body | ConvertFrom-Json))
            return
        }
        return $script:intakeItem
    }

    if ($Method -eq "Patch" -and $Uri -eq "https://api.github.com/repos/cihanduruer/agentic-development/issues/5") {
        $updatedIssue = $linkedIssue.PSObject.Copy()
        $updatedIssue.body = ($Body | ConvertFrom-Json).body
        return $updatedIssue
    }

    if ($Method -eq "Post" -and $Uri -in @(
        "https://api.github.com/repos/cihanduruer/agentic-development/issues",
        "https://api.github.com/repos/cihanduruer/agentic-development/issues/5/assignees"
    )) {
        $script:assignmentRequests.Add([PSCustomObject]@{
            Uri = $Uri
            Body = $Body | ConvertFrom-Json
        })
        if ($script:rejectModel) {
            throw "422 Unprocessable Entity: model gpt-6.1-sol is unavailable"
        }
        return [PSCustomObject]@{
            number = 5
            html_url = "https://github.com/cihanduruer/agentic-development/issues/5"
            assignees = @([PSCustomObject]@{ login = "copilot-swe-agent[bot]" })
        }
    }

    throw "Unexpected REST request: $Method $Uri"
}

$originalAgentToken = $env:COPILOT_AGENT_TOKEN
try {
    $env:COPILOT_AGENT_TOKEN = "mock-github-token"
    foreach ($existingIssue in @($false, $true)) {
        foreach ($rejectModel in @($false, $true)) {
            $script:rejectModel = $rejectModel
            $script:intakeIssues = if ($existingIssue) { @($linkedIssue) } else { @($unrelatedIssue, $spoofedLink) }
            $script:intakeItem = [PSCustomObject]@{
                rev = 1
                fields = @{
                    "System.Title" = "Show total stay price before booking confirmation"
                    "System.State" = "New"
                    "System.WorkItemType" = "User Story"
                    "System.TeamProject" = "sample-project"
                    "System.Tags" = "ready-for-triage"
                    "System.Description" = "Guests can review the total stay price."
                }
                relations = @()
            }
            $script:assignmentRequests = [System.Collections.Generic.List[object]]::new()
            $script:boardPatches = [System.Collections.Generic.List[object]]::new()
            $results = [System.Collections.Generic.List[object]]::new()
            $invokeIntake = {
                Invoke-AgenticIntake `
                    -Organization "ai-enabled-ado-org" `
                    -Project "sample-project" `
                    -Repository "cihanduruer/agentic-development" `
                    -BaseBranch "main" `
                    -WorkItemId @(959) | ForEach-Object { $results.Add($_) }
            }

            if ($rejectModel) {
                Assert-Throws -Action $invokeIntake -ExpectedMessage "422 Unprocessable Entity: model gpt-6.1-sol is unavailable"
                Assert-True -Condition ($results.Count -eq 0) -Message "Rejected assignment must not report success."
                Assert-True -Condition ($script:boardPatches.Count -eq 0) -Message "Rejected assignment must not activate or tag the Board item."
            }
            else {
                & $invokeIntake
                Assert-True `
                    -Condition ($results.Count -eq 1 -and $results[0].CopilotAssigned -and $results[0].BoardUpdated) `
                    -Message "Accepted assignment should report verified assignment and Board update."
                Assert-True -Condition ($script:boardPatches.Count -eq 1) -Message "Accepted assignment should update Boards once."
                $statePatch = @($script:boardPatches[0] | Where-Object { $_.path -eq "/fields/System.State" })
                Assert-True -Condition ($statePatch.Count -eq 1 -and $statePatch[0].value -eq "Active") -Message "Verified assignment should activate the New Board item."
            }

            Assert-True -Condition ($script:assignmentRequests.Count -eq 1) -Message "Each path must send exactly one assignment request without fallback retries."
            $request = $script:assignmentRequests[0]
            $expectedUri = if ($existingIssue) {
                "https://api.github.com/repos/cihanduruer/agentic-development/issues/5/assignees"
            }
            else {
                "https://api.github.com/repos/cihanduruer/agentic-development/issues"
            }
            Assert-True -Condition ($request.Uri -eq $expectedUri) -Message "Intake must use the expected new-issue or existing-issue assignment path."
            Assert-True -Condition ($request.Body.agent_assignment.model -ceq "gpt-6.1-sol") -Message "Both assignment paths must send the explicit GPT 6.1 Sol model."
            Assert-True `
                -Condition ($request.Body.agent_assignment.custom_agent -eq "hotel-developer" -and $request.Body.agent_assignment.base_branch -eq "main" -and $request.Body.agent_assignment.target_repo -eq "cihanduruer/agentic-development") `
                -Message "Explicit model selection must preserve the agent, base branch, and target repository."
        }
    }
}
finally {
    $env:COPILOT_AGENT_TOKEN = $originalAgentToken
}

Write-Output "Start-AgenticWork tests passed."
