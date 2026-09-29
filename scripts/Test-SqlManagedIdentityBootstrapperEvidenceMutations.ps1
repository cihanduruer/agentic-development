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
