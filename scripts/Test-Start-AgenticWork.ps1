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
    body = "Azure Boards work item: [AB#959](https://dev.azure.com/example)"
}
$selected = Select-WorkItemIssue -Issues @($linkedIssue) -Id 959
Assert-True -Condition ($selected.number -eq 5) -Message "AB#959 should resolve to existing issue #5."

$unrelatedIssue = [PSCustomObject]@{
    number = 6
    title = "[AB#960] Unrelated work"
    body = "Azure Boards work item: [AB#960](https://dev.azure.com/example)"
}
$missing = Select-WorkItemIssue -Issues @($unrelatedIssue) -Id 959
Assert-True -Condition ($null -eq $missing) -Message "Unrelated Board items must not be selected."

Assert-Throws `
    -Action { Select-WorkItemIssue -Issues @($linkedIssue, $linkedIssue.PSObject.Copy()) -Id 959 } `
    -ExpectedMessage "Multiple GitHub issues link AB#959"

Write-Output "Start-AgenticWork tests passed."
