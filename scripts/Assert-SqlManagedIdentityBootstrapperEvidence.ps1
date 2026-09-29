[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $TrxPath
)

$ErrorActionPreference = 'Stop'
$expectedTotal = 22

if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
    throw "SQL bootstrapper TRX '$TrxPath' does not exist."
}

[xml] $trx = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $TrxPath))
$counters = $trx.TestRun.ResultSummary.Counters
if ($null -eq $counters) {
    throw "SQL bootstrapper TRX '$TrxPath' has no result counters."
}

function Get-Counter {
    param([Parameter(Mandatory)][string] $Name)

    $value = $counters.GetAttribute($Name)
    if ($value -notmatch '^\d+$') {
        throw "SQL bootstrapper TRX counter '$Name' is missing or invalid."
    }

    return [int] $value
}

$requiredCounters = @{
    total = $expectedTotal
    executed = $expectedTotal
    passed = $expectedTotal
    failed = 0
    error = 0
    timeout = 0
    aborted = 0
    inconclusive = 0
    passedButRunAborted = 0
    notRunnable = 0
    notExecuted = 0
    disconnected = 0
    warning = 0
    completed = 0
    inProgress = 0
    pending = 0
}
foreach ($entry in $requiredCounters.GetEnumerator()) {
    $actual = Get-Counter -Name $entry.Key
    if ($actual -ne $entry.Value) {
        throw "SQL bootstrapper TRX counter '$($entry.Key)' is $actual; expected $($entry.Value)."
    }
}

$results = @($trx.TestRun.Results.UnitTestResult)
if ($results.Count -ne $expectedTotal) {
    throw "SQL bootstrapper TRX contains $($results.Count) results; expected $expectedTotal."
}
if (@(
        $results |
            Where-Object {
                -not [string]::Equals(
                    [string] $_.outcome,
                    'Passed',
                    [StringComparison]::Ordinal)
            }
    ).Count -gt 0) {
    throw 'SQL bootstrapper TRX contains a result that is not Passed.'
}

$testClassPrefix =
    'AgenticHotelBooking.IntegrationTests.' +
    'SqlManagedIdentityBootstrapperSqlServerTests.'
$expectedIdentities = @(
    "${testClassPrefix}ExactDirectPermissionContractMigratesAndRerunsIdempotently"
    "${testClassPrefix}MutatedDirectPermissionStateFailsClosed(mutation: `"subset`")"
    "${testClassPrefix}MutatedDirectPermissionStateFailsClosed(mutation: `"superset`")"
    "${testClassPrefix}MutatedDirectPermissionStateFailsClosed(mutation: `"deny`")"
    "${testClassPrefix}MutatedDirectPermissionStateFailsClosed(mutation: `"grant-option`")"
    "${testClassPrefix}MutatedDirectPermissionStateFailsClosed(mutation: `"column`")"
    "${testClassPrefix}UnexpectedRuntimeRoleOwnerFailsClosed(ownerType: `"user`")"
    "${testClassPrefix}UnexpectedRuntimeRoleOwnerFailsClosed(ownerType: `"role`")"
    "${testClassPrefix}UnexpectedRuntimeRoleMemberFailsClosed"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"ALTER`", state: `"G`", granteeType: `"user`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"ALTER`", state: `"G`", granteeType: `"role`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"ALTER`", state: `"W`", granteeType: `"user`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"ALTER`", state: `"W`", granteeType: `"role`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"CONTROL`", state: `"G`", granteeType: `"user`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"CONTROL`", state: `"G`", granteeType: `"role`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"CONTROL`", state: `"W`", granteeType: `"user`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"CONTROL`", state: `"W`", granteeType: `"role`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"TAKE OWNERSHIP`", state: `"G`", granteeType: `"user`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"TAKE OWNERSHIP`", state: `"G`", granteeType: `"role`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"TAKE OWNERSHIP`", state: `"W`", granteeType: `"user`")"
    "${testClassPrefix}DelegatedRuntimeRolePermissionFailsClosed(permissionName: `"TAKE OWNERSHIP`", state: `"W`", granteeType: `"role`")"
    "${testClassPrefix}FailureImmediatelyBeforeCommitRollsBackEveryMutation"
)
$expectedIdentitySet =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($expectedIdentity in $expectedIdentities) {
    if (-not $expectedIdentitySet.Add($expectedIdentity)) {
        throw "Duplicate expected SQL test identity '$expectedIdentity'."
    }
}
$actualIdentitySet =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($result in $results) {
    if (-not $actualIdentitySet.Add([string] $result.testName)) {
        throw "SQL bootstrapper TRX contains duplicate test identity '$($result.testName)'."
    }
}
if (-not $actualIdentitySet.SetEquals($expectedIdentitySet)) {
    $missing = @($expectedIdentities | Where-Object { -not $actualIdentitySet.Contains($_) })
    $unexpected = @($actualIdentitySet | Where-Object { -not $expectedIdentitySet.Contains($_) })
    throw "SQL bootstrapper TRX identities differ. Missing: [$($missing -join '; ')]. Unexpected: [$($unexpected -join '; ')]."
}

function Get-CanonicalGuid {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string] $Value,

        [Parameter(Mandatory)]
        [string] $Label
    )

    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Value, [ref] $parsed)) {
        throw "SQL bootstrapper TRX $Label '$Value' is not a GUID."
    }

    return $parsed.ToString('D')
}

$definitions = @($trx.TestRun.TestDefinitions.UnitTest)
$entries = @($trx.TestRun.TestEntries.TestEntry)
if ($definitions.Count -ne $expectedTotal -or $entries.Count -ne $expectedTotal) {
    throw "SQL bootstrapper TRX must contain exactly $expectedTotal definitions and entries."
}

$definitionsByTestId = @{}
$definitionExecutionIds =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($definition in $definitions) {
    $testId = Get-CanonicalGuid -Value $definition.id -Label 'definition test ID'
    $executionId =
        Get-CanonicalGuid `
            -Value $definition.Execution.id `
            -Label 'definition execution ID'
    if ($definitionsByTestId.ContainsKey($testId)) {
        throw "SQL bootstrapper TRX contains duplicate definition test ID '$testId'."
    }
    if (-not $definitionExecutionIds.Add($executionId)) {
        throw "SQL bootstrapper TRX contains duplicate definition execution ID '$executionId'."
    }
    if (-not $expectedIdentitySet.Contains([string] $definition.name)) {
        throw "SQL bootstrapper TRX contains unexpected definition '$($definition.name)'."
    }
    if (-not [string]::Equals(
            [string] $definition.TestMethod.className,
            'AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests',
            [StringComparison]::Ordinal)) {
        throw "SQL bootstrapper TRX definition '$($definition.name)' has an unexpected class."
    }
    $identityWithoutClass =
        ([string] $definition.name).Substring(
            'AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests.'.Length)
    $expectedMethodName = ($identityWithoutClass -split '\(', 2)[0]
    if (-not [string]::Equals(
            [string] $definition.TestMethod.name,
            $expectedMethodName,
            [StringComparison]::Ordinal)) {
        throw "SQL bootstrapper TRX definition '$($definition.name)' has an unexpected method."
    }

    $definitionsByTestId[$testId] = @{
        Definition = $definition
        ExecutionId = $executionId
    }
}

$entriesByTestId = @{}
$entryExecutionIds =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $entries) {
    $testId = Get-CanonicalGuid -Value $entry.testId -Label 'entry test ID'
    $executionId =
        Get-CanonicalGuid -Value $entry.executionId -Label 'entry execution ID'
    if ($entriesByTestId.ContainsKey($testId)) {
        throw "SQL bootstrapper TRX contains duplicate entry test ID '$testId'."
    }
    if (-not $entryExecutionIds.Add($executionId)) {
        throw "SQL bootstrapper TRX contains duplicate entry execution ID '$executionId'."
    }

    $entriesByTestId[$testId] = $executionId
}

$resultTestIds =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$resultExecutionIds =
    [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($result in $results) {
    $testId = Get-CanonicalGuid -Value $result.testId -Label 'result test ID'
    $executionId =
        Get-CanonicalGuid -Value $result.executionId -Label 'result execution ID'
    if (-not $resultTestIds.Add($testId)) {
        throw "SQL bootstrapper TRX contains duplicate result test ID '$testId'."
    }
    if (-not $resultExecutionIds.Add($executionId)) {
        throw "SQL bootstrapper TRX contains duplicate result execution ID '$executionId'."
    }
    if (-not $definitionsByTestId.ContainsKey($testId)) {
        throw "SQL bootstrapper TRX result '$($result.testName)' has no matching definition."
    }
    if (-not $entriesByTestId.ContainsKey($testId)) {
        throw "SQL bootstrapper TRX result '$($result.testName)' has no matching entry."
    }

    $definitionLink = $definitionsByTestId[$testId]
    if (-not [string]::Equals(
            [string] $definitionLink.Definition.name,
            [string] $result.testName,
            [StringComparison]::Ordinal)) {
        throw "SQL bootstrapper TRX result '$($result.testName)' does not match its definition."
    }
    if (-not [string]::Equals(
            [string] $definitionLink.ExecutionId,
            $executionId,
            [StringComparison]::Ordinal) -or
        -not [string]::Equals(
            [string] $entriesByTestId[$testId],
            $executionId,
            [StringComparison]::Ordinal)) {
        throw "SQL bootstrapper TRX result '$($result.testName)' has inconsistent execution linkage."
    }
}

Write-Host "SQL bootstrapper evidence passed: $expectedTotal exact tests executed and passed."
