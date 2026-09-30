$ErrorActionPreference = 'Stop'

$assertionPath =
    Join-Path $PSScriptRoot 'Assert-SqlManagedIdentityBootstrapperEvidence.ps1'
$selfTestPath =
    Join-Path $PSScriptRoot 'Test-Assert-SqlManagedIdentityBootstrapperEvidence.ps1'
$originalBytes = [IO.File]::ReadAllBytes($assertionPath)
$utf8 = [Text.UTF8Encoding]::new($false)
$original = $utf8.GetString($originalBytes)
$shellPath = (Get-Process -Id $PID).Path

$mutations = @(
    @{
        Name = 'weaken-exact-case-identities'
        ExpectedFailure = "Challenge '03-duplicate-delegated-case' was accepted."
        Replacements = @(
            @{
                Pattern =
                    '(?s)\$expectedIdentitySet =.*?\r?\nif \(-not \$actualIdentitySet\.SetEquals\(\$expectedIdentitySet\)\) \{.*?\r?\n\}'
                Value =
                    '$expectedIdentitySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)'
            },
            @{
                Pattern =
                    "(?s)\r?\n    if \(-not \`$expectedIdentitySet\.Contains\(\[string\] \`$definition\.name\)\) \{\r?\n        throw .*?\r?\n    \}"
                Value = ''
            }
        )
    },
    @{
        Name = 'remove-identifier-uniqueness-and-linkage'
        ExpectedFailure = "Challenge '13-duplicate-result-test-id' was accepted."
        Replacements = @(
            @{
                Pattern =
                    '(?s)\r?\n\$definitions = @\(\$trx\.TestRun\.TestDefinitions\.UnitTest\).*?\r?\n\}\r?\n\r?\nWrite-Host'
                Value = "`r`n`r`nWrite-Host"
            }
        )
    },
    @{
        Name = 'case-insensitive-outcome'
        Replacements = @(
            @{
                Pattern =
                    "(?s)-not \[string\]::Equals\(\r?\n                    \[string\] \`$_\.outcome,\r?\n                    'Passed',\r?\n                    \[StringComparison\]::Ordinal\)"
                Value = '([string] $_.outcome) -ne ''Passed'''
            }
        )
        ExpectedFailure = "Challenge '29-outcome-casing' was accepted."
    },
    @{
        Name = 'case-insensitive-identities'
        ExpectedFailure = "Challenge '10-wrong-casing' was accepted."
        Replacements = @(
            @{
                Pattern =
                    '\$expectedIdentitySet =\r?\n    \[Collections\.Generic\.HashSet\[string\]\]::new\(\[StringComparer\]::Ordinal\)'
                Value =
                    '$expectedIdentitySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)'
            },
            @{
                Pattern =
                    '\$actualIdentitySet =\r?\n    \[Collections\.Generic\.HashSet\[string\]\]::new\(\[StringComparer\]::Ordinal\)'
                Value =
                    '$actualIdentitySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)'
            }
        )
    },
    @{
        Name = 'case-insensitive-definition-class'
        Replacements = @(
            @{
                Pattern =
                    "(?s)-not \[string\]::Equals\(\r?\n            \[string\] \`$definition\.TestMethod\.className,\r?\n            'AgenticHotelBooking\.IntegrationTests\.SqlManagedIdentityBootstrapperSqlServerTests',\r?\n            \[StringComparison\]::Ordinal\)"
                Value =
                    '$definition.TestMethod.className -ne ''AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests'''
            }
        )
        ExpectedFailure = "Challenge '32-definition-class-casing' was accepted."
    },
    @{
        Name = 'disable-guid-parsing'
        ExpectedFailure = "Challenge '39-linked-invalid-test-guid' was accepted."
        Replacements = @(
            @{
                Pattern =
                    "(?s)    \`$parsed = \[guid\]::Empty\r?\n    if \(-not \[guid\]::TryParse\(\`$Value, \[ref\] \`$parsed\)\) \{.*?\r?\n    \}\r?\n\r?\n    return \`$parsed\.ToString\('D'\)"
                Value = '    return $Value'
            }
        )
    },
    @{
        Name = 'remove-result-definition-name-link'
        ExpectedFailure = "Challenge '41-swapped-same-method-definitions' was accepted."
        Replacements = @(
            @{
                Pattern =
                    "(?s)\r?\n    if \(-not \[string\]::Equals\(\r?\n            \[string\] \`$definitionLink\.Definition\.name,\r?\n            \[string\] \`$result\.testName,\r?\n            \[StringComparison\]::Ordinal\)\) \{\r?\n        throw .*?\r?\n    \}"
                Value = ''
            }
        )
    }
)

function Assert-TargetedChallenge {
    param(
        [Parameter(Mandatory)][string] $ExpectedFailure,
        [Parameter(Mandatory)][int] $ExitCode,
        [Parameter(Mandatory)][string] $Output
    )

    if ($ExitCode -eq 0 -or
        $Output.IndexOf($ExpectedFailure, [StringComparison]::Ordinal) -lt 0) {
        throw "Expected targeted failure '$ExpectedFailure', exit code ${ExitCode}: $Output"
    }
}

$probeFailure = "Challenge 'probe' was accepted."
Assert-TargetedChallenge -ExpectedFailure $probeFailure -ExitCode 1 -Output $probeFailure
foreach ($probe in @(
    @{ ExitCode = 0; Output = $probeFailure }
    @{ ExitCode = 1; Output = 'Unrelated subprocess failure.' }
    @{ ExitCode = 1; Output = "Challenge 'different' was accepted." }
    @{ ExitCode = 1; Output = $probeFailure.ToUpperInvariant() }
)) {
    $rejected = $false
    try {
        Assert-TargetedChallenge -ExpectedFailure $probeFailure @probe
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'The targeted-challenge gate accepted an unrelated or successful result.'
    }
}

try {
    foreach ($mutation in $mutations) {
        $mutated = $original
        foreach ($replacement in $mutation.Replacements) {
            $regex = [regex]::new($replacement.Pattern)
            if ($regex.Matches($mutated).Count -ne 1) {
                throw "Evidence mutation '$($mutation.Name)' did not match exactly one block."
            }

            $literalReplacement = [string] $replacement.Value
            $mutated = $regex.Replace(
                $mutated,
                [Text.RegularExpressions.MatchEvaluator] {
                    param($match)
                    return $literalReplacement
                },
                1)
        }

        [IO.File]::WriteAllText($assertionPath, $mutated, $utf8)
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $baselineOutput = & $shellPath `
                -NoProfile `
                -File $selfTestPath `
                -ValidEvidenceOnly 2>&1
            $baselineExitCode = $LASTEXITCODE
            if ($baselineExitCode -ne 0) {
                throw "Evidence mutation '$($mutation.Name)' rejected the valid baseline: $baselineOutput"
            }

            $output = & $shellPath -NoProfile -File $selfTestPath 2>&1
            $testExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        [IO.File]::WriteAllBytes($assertionPath, $originalBytes)

        Assert-TargetedChallenge `
            -ExpectedFailure $mutation.ExpectedFailure `
            -ExitCode $testExitCode `
            -Output ($output -join [Environment]::NewLine)

        Write-Host "Evidence mutation '$($mutation.Name)' accepted the valid baseline and was rejected by its challenge: $($output[0])"
    }
}
finally {
    [IO.File]::WriteAllBytes($assertionPath, $originalBytes)
}

Write-Host 'SQL bootstrapper evidence mutation tests passed.'
exit 0
