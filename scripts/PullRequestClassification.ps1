$ErrorActionPreference = 'Stop'

function Get-PullRequestFieldValues {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Body,

        [Parameter(Mandatory)]
        [string] $Field
    )

    $escapedField = [Regex]::Escape($Field)
    return @(
        [Regex]::Matches(
            $Body,
            "(?im)^\s*-\s*$escapedField\s*:\s*(?<value>[^\r\n]*)\r?$") |
            ForEach-Object { $_.Groups['value'].Value.Trim() }
    )
}

function Get-AbIds {
    param([AllowEmptyString()][string] $Text)

    return @(
        [Regex]::Matches($Text, '(?i)\bAB#(?<id>[1-9][0-9]*)\b') |
            ForEach-Object { [int]$_.Groups['id'].Value } |
            Sort-Object -Unique
    )
}

function Get-PullRequestMetadataDigest {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Title,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Body
    )

    $bytes = [Text.Encoding]::UTF8.GetBytes("$Title`n$Body")
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-PullRequestClassification {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Title,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Body
    )

    $platformValues = @(Get-PullRequestFieldValues -Body $Body -Field 'Platform change')
    if ($platformValues.Count -eq 0) {
        throw "Pull request metadata must declare '- Platform change: true' or '- Platform change: false'."
    }

    $normalizedPlatformValues = @(
        $platformValues | ForEach-Object {
            if ($_ -notmatch '^(?i:true|false)$') {
                throw "Platform change declarations must be exactly 'true' or 'false'."
            }
            $_.ToLowerInvariant()
        } | Sort-Object -Unique
    )
    if ($normalizedPlatformValues.Count -ne 1) {
        throw 'Pull request metadata has contradictory Platform change declarations.'
    }

    $boardsValues = @(Get-PullRequestFieldValues -Body $Body -Field 'Azure Boards')
    $boardsNotApplicable = @(
        $boardsValues | Where-Object { $_ -match '^N/A(?:\s*[.;]|$)' }
    ).Count -gt 0
    $ids = @(Get-AbIds -Text "$Title`n$($boardsValues -join "`n")")
    $isPlatformChange = $normalizedPlatformValues[0] -eq 'true'

    if ($boardsNotApplicable -and $ids.Count -gt 0) {
        throw 'Pull request metadata has conflicting Azure Boards N/A and AB identity declarations.'
    }
    if ($boardsNotApplicable -and -not $isPlatformChange) {
        throw "Azure Boards N/A requires '- Platform change: true'."
    }
    if ($ids.Count -eq 0 -and -not $isPlatformChange) {
        throw 'Pull request has no Azure Boards identity and is not explicitly marked as a platform change.'
    }
    if ($ids.Count -gt 1) {
        throw "Pull request must identify exactly one Azure Boards item; found $($ids.Count)."
    }

    return [pscustomobject]@{
        IsPlatformChange = $isPlatformChange
        IsPlatformOnly = $isPlatformChange -and $ids.Count -eq 0
        BoardsNotApplicable = $boardsNotApplicable
        WorkItemIds = $ids
    }
}
