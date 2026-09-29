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
                Value = '([string] $$_.outcome) -ne ''Passed'''
            }
        )
    },
    @{
        Name = 'case-insensitive-identities'
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
                    '$$definition.TestMethod.className -ne ''AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests'''
            }
        )
    },
    @{
        Name = 'disable-guid-parsing'
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
        Replacements = @(
            @{
                Pattern =
                    "(?s)\r?\n    if \(-not \[string\]::Equals\(\r?\n            \[string\] \`$definitionLink\.Definition\.name,\r?\n            \[string\] \`$result\.testName,\r?\n            \[StringComparison\]::Ordinal\)\) \{\r?\n        throw .*?\r?\n    \}"
                Value = ''
            }
        )
    }
)

try {
    foreach ($mutation in $mutations) {
        $mutated = $original
        foreach ($replacement in $mutation.Replacements) {
            $regex = [regex]::new($replacement.Pattern)
            if ($regex.Matches($mutated).Count -ne 1) {
                throw "Evidence mutation '$($mutation.Name)' did not match exactly one block."
            }

            $mutated = $regex.Replace($mutated, $replacement.Value, 1)
        }

        [IO.File]::WriteAllText($assertionPath, $mutated, $utf8)
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & $shellPath -NoProfile -File $selfTestPath 2>&1
            $testExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        [IO.File]::WriteAllBytes($assertionPath, $originalBytes)

        if ($testExitCode -eq 0) {
            throw "Evidence mutation '$($mutation.Name)' survived the self-test."
        }

        Write-Host "Evidence mutation '$($mutation.Name)' was rejected: $($output[0])"
    }
}
finally {
    [IO.File]::WriteAllBytes($assertionPath, $originalBytes)
}

Write-Host 'SQL bootstrapper evidence mutation tests passed.'
exit 0
