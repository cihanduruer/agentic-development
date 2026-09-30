[CmdletBinding()]
param(
    [switch] $ValidEvidenceOnly
)

$ErrorActionPreference = 'Stop'

$assertionScript = Join-Path $PSScriptRoot 'Assert-SqlManagedIdentityBootstrapperEvidence.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "sql-bootstrap-evidence-$([guid]::NewGuid().ToString('N'))"
$shellPath = (Get-Process -Id $PID).Path
$testClass =
    'AgenticHotelBooking.IntegrationTests.' +
    'SqlManagedIdentityBootstrapperSqlServerTests'
$testClassPrefix = "$testClass."
$expectedNames = @(
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
    "${testClassPrefix}IndirectApiMembershipDoesNotSatisfyDirectMembershipContract"
    "${testClassPrefix}FailureImmediatelyBeforeCommitRollsBackEveryMutation"
    "${testClassPrefix}ExistingApiPrincipalIdentityMismatchFailsClosed(mutation: `"name`")"
    "${testClassPrefix}ExistingApiPrincipalIdentityMismatchFailsClosed(mutation: `"sid`")"
    "${testClassPrefix}ExistingApiPrincipalIdentityMismatchFailsClosed(mutation: `"type`")"
    "${testClassPrefix}ExistingApiPrincipalIdentityMismatchFailsClosed(mutation: `"authentication`")"
    "${testClassPrefix}DistinctNameAndSidMatchesFailClosedWithAmbiguousDiagnostic"
    "${testClassPrefix}CanonicalConnectIsPreservedAcrossBootstrapAndRerun(initialState: `"baseline-only`")"
    "${testClassPrefix}CanonicalConnectIsPreservedAcrossBootstrapAndRerun(initialState: `"exact-seven`")"
    "${testClassPrefix}NoncanonicalConnectOrDatabasePermissionFailsClosed(mutation: `"deny`")"
    "${testClassPrefix}NoncanonicalConnectOrDatabasePermissionFailsClosed(mutation: `"grant-option`")"
    "${testClassPrefix}NoncanonicalConnectOrDatabasePermissionFailsClosed(mutation: `"grantor`")"
    "${testClassPrefix}NoncanonicalConnectOrDatabasePermissionFailsClosed(mutation: `"extra-database-permission`")"
)

function New-CaseSet {
    param([Parameter(Mandatory)][string[]] $Names)

    foreach ($name in $Names) {
        $testId = [guid]::NewGuid().ToString('D')
        $executionId = [guid]::NewGuid().ToString('D')
        [pscustomobject] @{
            ResultName = $name
            DefinitionName = $name
            ResultTestId = $testId
            DefinitionTestId = $testId
            EntryTestId = $testId
            ResultExecutionId = $executionId
            DefinitionExecutionId = $executionId
            EntryExecutionId = $executionId
            DefinitionClass = $testClass
            DefinitionMethod = Get-MethodName $name
            Outcome = 'Passed'
            IncludeResult = $true
            IncludeDefinition = $true
            IncludeEntry = $true
        }
    }
}

function Copy-CaseSet {
    param([Parameter(Mandatory)][object[]] $Cases)

    foreach ($case in $Cases) {
        [pscustomobject] @{
            ResultName = $case.ResultName
            DefinitionName = $case.DefinitionName
            ResultTestId = $case.ResultTestId
            DefinitionTestId = $case.DefinitionTestId
            EntryTestId = $case.EntryTestId
            ResultExecutionId = $case.ResultExecutionId
            DefinitionExecutionId = $case.DefinitionExecutionId
            EntryExecutionId = $case.EntryExecutionId
            DefinitionClass = $case.DefinitionClass
            DefinitionMethod = $case.DefinitionMethod
            Outcome = $case.Outcome
            IncludeResult = $case.IncludeResult
            IncludeDefinition = $case.IncludeDefinition
            IncludeEntry = $case.IncludeEntry
        }
    }
}

function Escape-Xml {
    param([AllowEmptyString()][string] $Value)

    return [Security.SecurityElement]::Escape($Value)
}

function Get-MethodName {
    param([Parameter(Mandatory)][string] $Identity)

    if ($Identity -match '\.(?<Method>[A-Za-z_][A-Za-z0-9_]*)(?:\(.*\))?$') {
        return $Matches.Method
    }

    return 'InvalidMethod'
}

function New-TestTrx {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Cases,
        [int] $Total = 34,
        [int] $Executed = 34,
        [int] $Passed = 34,
        [int] $NotExecuted = 0
    )

    $results = foreach ($case in $Cases) {
        if ($case.IncludeResult) {
            '<UnitTestResult executionId="{0}" testId="{1}" testName="{2}" outcome="{3}" />' -f
                (Escape-Xml $case.ResultExecutionId),
                (Escape-Xml $case.ResultTestId),
                (Escape-Xml $case.ResultName),
                (Escape-Xml $case.Outcome)
        }
    }
    $definitions = foreach ($case in $Cases) {
        if ($case.IncludeDefinition) {
            @'
<UnitTest name="{0}" id="{1}">
  <Execution id="{2}" />
  <TestMethod className="{3}" name="{4}" />
</UnitTest>
'@ -f
                (Escape-Xml $case.DefinitionName),
                (Escape-Xml $case.DefinitionTestId),
                (Escape-Xml $case.DefinitionExecutionId),
                (Escape-Xml $case.DefinitionClass),
                (Escape-Xml $case.DefinitionMethod)
        }
    }
    $entries = foreach ($case in $Cases) {
        if ($case.IncludeEntry) {
            '<TestEntry testId="{0}" executionId="{1}" />' -f
                (Escape-Xml $case.EntryTestId),
                (Escape-Xml $case.EntryExecutionId)
        }
    }
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  <Results>$($results -join [Environment]::NewLine)</Results>
  <TestDefinitions>$($definitions -join [Environment]::NewLine)</TestDefinitions>
  <TestEntries>$($entries -join [Environment]::NewLine)</TestEntries>
  <ResultSummary>
    <Counters total="$Total" executed="$Executed" passed="$Passed" failed="0"
      error="0" timeout="0" aborted="0" inconclusive="0"
      passedButRunAborted="0" notRunnable="0" notExecuted="$NotExecuted"
      disconnected="0" warning="0" completed="0" inProgress="0"
      pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Invoke-Assertion {
    param([Parameter(Mandatory)][string] $Path)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $shellPath -NoProfile -File $assertionScript -TrxPath $Path 2>&1
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return @{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Assert-Rejected {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]] $Cases,
        [int] $Total = 34,
        [int] $Executed = 34,
        [int] $Passed = 34,
        [int] $NotExecuted = 0
    )

    $path = Join-Path $testRoot "$Name.trx"
    New-TestTrx `
        -Path $path `
        -Cases $Cases `
        -Total $Total `
        -Executed $Executed `
        -Passed $Passed `
        -NotExecuted $NotExecuted
    $result = Invoke-Assertion -Path $path
    if ($result.ExitCode -eq 0) {
        throw "Challenge '$Name' was accepted."
    }
}

$baseCases = @(New-CaseSet -Names $expectedNames)
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $validPath = Join-Path $testRoot 'valid.trx'
    New-TestTrx -Path $validPath -Cases $baseCases
    $valid = Invoke-Assertion -Path $validPath
    if ($valid.ExitCode -ne 0) {
        throw "Valid SQL evidence was rejected: $($valid.Output)"
    }
    if ($ValidEvidenceOnly) {
        Write-Host 'Valid SQL evidence baseline passed.'
        exit 0
    }

    $permutedPath = Join-Path $testRoot 'permuted.trx'
    New-TestTrx -Path $permutedPath -Cases @($baseCases | Sort-Object ResultName -Descending)
    $permuted = Invoke-Assertion -Path $permutedPath
    if ($permuted.ExitCode -ne 0) {
        throw "Permuted or XML-encoded SQL evidence was rejected: $($permuted.Output)"
    }

    $uppercaseIds = @(Copy-CaseSet $baseCases)
    $uppercaseIds | ForEach-Object {
        $_.ResultTestId = $_.ResultTestId.ToUpperInvariant()
        $_.DefinitionTestId = $_.DefinitionTestId.ToUpperInvariant()
        $_.EntryTestId = $_.EntryTestId.ToUpperInvariant()
        $_.ResultExecutionId = $_.ResultExecutionId.ToUpperInvariant()
        $_.DefinitionExecutionId = $_.DefinitionExecutionId.ToUpperInvariant()
        $_.EntryExecutionId = $_.EntryExecutionId.ToUpperInvariant()
    }
    $uppercaseIdsPath = Join-Path $testRoot 'uppercase-canonical-guids.trx'
    New-TestTrx -Path $uppercaseIdsPath -Cases $uppercaseIds
    $uppercaseIdsResult = Invoke-Assertion -Path $uppercaseIdsPath
    if ($uppercaseIdsResult.ExitCode -ne 0) {
        throw "Canonical uppercase GUID evidence was rejected: $($uppercaseIdsResult.Output)"
    }

    Assert-Rejected -Name '01-zero-executed' -Cases @() -Total 0 -Executed 0 -Passed 0

    $cases = @(Copy-CaseSet $baseCases)
    $cases | ForEach-Object { $_.Outcome = 'NotExecuted' }
    Assert-Rejected -Name '02-all-not-executed' -Cases $cases -Executed 0 -Passed 0 -NotExecuted 34

    $cases = @(Copy-CaseSet $baseCases)
    $cases[10].ResultName = $cases[9].ResultName
    $cases[10].DefinitionName = $cases[9].DefinitionName
    Assert-Rejected -Name '03-duplicate-delegated-case' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    9..20 | ForEach-Object {
        $cases[$_].ResultName = $cases[9].ResultName
        $cases[$_].DefinitionName = $cases[9].DefinitionName
    }
    Assert-Rejected -Name '04-all-delegated-cases-identical' -Cases $cases

    foreach ($challenge in @(
        @{ Name = '05-wrong-permission'; Index = 9; Old = 'ALTER'; New = 'UPDATE' }
        @{ Name = '06-wrong-state'; Index = 9; Old = 'state: "G"'; New = 'state: "D"' }
        @{ Name = '07-wrong-grantee'; Index = 9; Old = 'granteeType: "user"'; New = 'granteeType: "admin"' }
        @{ Name = '08-wrong-owner'; Index = 6; Old = 'ownerType: "user"'; New = 'ownerType: "attacker"' }
        @{ Name = '09-wrong-mutation'; Index = 1; Old = 'mutation: "subset"'; New = 'mutation: "prefix"' }
        @{ Name = '10-wrong-casing'; Index = 9; Old = 'permissionName: "ALTER"'; New = 'permissionName: "alter"' }
        @{ Name = '11-suffixed-method'; Index = 22; Old = 'EveryMutation'; New = 'EveryMutationDisabled' }
        @{ Name = '12-extra-argument-space'; Index = 9; Old = ', state:'; New = ' , state:' }
    )) {
        $cases = @(Copy-CaseSet $baseCases)
        $cases[$challenge.Index].ResultName =
            $cases[$challenge.Index].ResultName.Replace($challenge.Old, $challenge.New)
        $cases[$challenge.Index].DefinitionName = $cases[$challenge.Index].ResultName
        Assert-Rejected -Name $challenge.Name -Cases $cases
    }

    $cases = @(Copy-CaseSet $baseCases)
    $cases[1].ResultTestId = $cases[0].ResultTestId
    Assert-Rejected -Name '13-duplicate-result-test-id' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[1].ResultExecutionId = $cases[0].ResultExecutionId
    Assert-Rejected -Name '14-duplicate-result-execution-id' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[1].DefinitionTestId = $cases[0].DefinitionTestId
    Assert-Rejected -Name '15-duplicate-definition-test-id' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[1].DefinitionExecutionId = $cases[0].DefinitionExecutionId
    Assert-Rejected -Name '16-duplicate-definition-execution-id' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[1].EntryTestId = $cases[0].EntryTestId
    Assert-Rejected -Name '17-duplicate-entry-test-id' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[1].EntryExecutionId = $cases[0].EntryExecutionId
    Assert-Rejected -Name '18-duplicate-entry-execution-id' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].ResultTestId = [guid]::NewGuid().ToString('D')
    Assert-Rejected -Name '19-result-definition-id-mismatch' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].ResultExecutionId = [guid]::NewGuid().ToString('D')
    Assert-Rejected -Name '20-result-execution-id-mismatch' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].DefinitionName = $cases[1].DefinitionName
    Assert-Rejected -Name '21-result-definition-name-mismatch' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].IncludeDefinition = $false
    Assert-Rejected -Name '22-missing-definition' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].IncludeEntry = $false
    Assert-Rejected -Name '23-missing-entry' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].ResultTestId = 'not-a-guid'
    Assert-Rejected -Name '24-invalid-result-guid' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].IncludeResult = $false
    Assert-Rejected -Name '25-missing-result' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases += @(New-CaseSet -Names @("${testClassPrefix}UnexpectedExtraTest"))
    Assert-Rejected -Name '26-extra-result' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].DefinitionClass = 'AgenticHotelBooking.IntegrationTests.OtherTests'
    Assert-Rejected -Name '27-wrong-definition-class' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].DefinitionMethod = 'UnexpectedMethod'
    Assert-Rejected -Name '28-wrong-definition-method' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].Outcome = 'passed'
    Assert-Rejected -Name '29-outcome-casing' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].ResultName = $cases[0].ResultName.ToLowerInvariant()
    Assert-Rejected -Name '30-result-identity-casing' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].DefinitionName = $cases[0].DefinitionName.ToLowerInvariant()
    Assert-Rejected -Name '31-definition-identity-casing' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].DefinitionClass = $cases[0].DefinitionClass.ToLowerInvariant()
    Assert-Rejected -Name '32-definition-class-casing' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].DefinitionMethod = $cases[0].DefinitionMethod.ToLowerInvariant()
    Assert-Rejected -Name '33-definition-method-casing' -Cases $cases

    foreach ($challenge in @(
        @{ Name = '34-state-value-casing'; Index = 9; Old = 'state: "G"'; New = 'state: "g"' }
        @{ Name = '35-grantee-value-casing'; Index = 9; Old = 'granteeType: "user"'; New = 'granteeType: "User"' }
        @{ Name = '36-owner-value-casing'; Index = 6; Old = 'ownerType: "user"'; New = 'ownerType: "User"' }
        @{ Name = '37-mutation-value-casing'; Index = 1; Old = 'mutation: "subset"'; New = 'mutation: "Subset"' }
        @{ Name = '38-argument-name-casing'; Index = 9; Old = 'permissionName:'; New = 'permissionname:' }
    )) {
        $cases = @(Copy-CaseSet $baseCases)
        $cases[$challenge.Index].ResultName =
            $cases[$challenge.Index].ResultName.Replace($challenge.Old, $challenge.New)
        $cases[$challenge.Index].DefinitionName = $cases[$challenge.Index].ResultName
        Assert-Rejected -Name $challenge.Name -Cases $cases
    }

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].ResultTestId = 'linked-invalid-test-id'
    $cases[0].DefinitionTestId = 'linked-invalid-test-id'
    $cases[0].EntryTestId = 'linked-invalid-test-id'
    Assert-Rejected -Name '39-linked-invalid-test-guid' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[0].ResultExecutionId = 'linked-invalid-execution-id'
    $cases[0].DefinitionExecutionId = 'linked-invalid-execution-id'
    $cases[0].EntryExecutionId = 'linked-invalid-execution-id'
    Assert-Rejected -Name '40-linked-invalid-execution-guid' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $firstDefinitionName = $cases[9].DefinitionName
    $cases[9].DefinitionName = $cases[10].DefinitionName
    $cases[10].DefinitionName = $firstDefinitionName
    Assert-Rejected -Name '41-swapped-same-method-definitions' -Cases $cases

    $cases = @(Copy-CaseSet $baseCases)
    $cases[27].ResultName = $cases[27].ResultName.Replace('Ambiguous', 'Unambiguous')
    $cases[27].DefinitionName = $cases[27].ResultName
    $cases[27].DefinitionMethod = Get-MethodName $cases[27].ResultName
    Assert-Rejected -Name '42-replaced-ambiguity-case' -Cases $cases

    Assert-Rejected -Name '43-missing-ambiguity-case' -Cases $baseCases[0..26 + 28..33] `
        -Total 33 -Executed 33 -Passed 33
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

Write-Host 'SQL bootstrapper evidence assertion tests passed: 3 valid variants and 43 challenges.'
exit 0
