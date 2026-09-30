[CmdletBinding()]
param(
    [string] $WorkflowPath =
        (Join-Path $PSScriptRoot '../.github/workflows/qa-evidence.yml'),

    [Parameter(Mandatory)]
    [string] $OraclePackagePath
)

$ErrorActionPreference = 'Stop'
$workflow = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $WorkflowPath))
$semanticValidator = Join-Path $PSScriptRoot 'assert_qa_test_selection.py'
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $semanticOutput = & python `
        -I `
        -S `
        $semanticValidator `
        $OraclePackagePath `
        (Resolve-Path -LiteralPath $WorkflowPath) 2>&1
    $semanticExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}
if ($semanticExitCode -ne 0) {
    throw "Semantic QA workflow validation failed: $semanticOutput"
}

$runDefaults = @(
    [regex]::Matches($workflow, '(?m)^(?:defaults:|    defaults:)(?:[ \t]*\r?$|[ \t]+[^\r\n]*\r?$)')
)
if ($runDefaults.Count -ne 0) {
    throw 'Workflow- and job-level defaults are not allowed for the QA command surface.'
}

$jobRunners = @(
    [regex]::Matches(
        $workflow,
        '(?m)^    runs-on:[ \t]*(?<value>[^\r\n]+?)[ \t]*\r?$')
)
if ($jobRunners.Count -ne 1 -or
    -not [string]::Equals(
        $jobRunners[0].Groups['value'].Value,
        'ubuntu-24.04',
        [StringComparison]::Ordinal)) {
    throw 'The QA job must run exactly once on ubuntu-24.04.'
}
if ([regex]::IsMatch($workflow, '(?m)^    container:')) {
    throw 'A job container is not allowed for the QA command surface.'
}

$unsupportedYamlForms = @(
    @{
        Pattern = '(?m)^[ \t]*(?:-[ \t]+)?[A-Za-z0-9_-]+[ \t]+:'
        Message = 'Whitespace before a YAML mapping-key colon is not supported by the QA workflow contract.'
    },
    @{
        Pattern = '(?m)^[ \t]*(?:-[ \t]+)?(?:"(?:\\.|[^"\r\n])*"|''(?:''''|[^''\r\n])*'')[ \t]*:'
        Message = 'Quoted YAML mapping keys are not supported by the QA workflow contract.'
    },
    @{
        Pattern = '(?m)^[ \t]*(?:-[ \t]+)?run[ \t]*:[ \t]*["'']'
        Message = 'Quoted inline run scalars are not supported by the QA workflow contract.'
    },
    @{
        Pattern = '(?m)^[ \t]*-[ \t]*\{'
        Message = 'Flow-style YAML step mappings are not supported by the QA workflow contract.'
    },
    @{
        Pattern = '(?m)^(?: {6}-[ \t]*\?[ \t]+| {8}\?[ \t]+)'
        Message = 'Explicit YAML step keys are not supported by the QA workflow contract.'
    },
    @{
        Pattern = '(?m)^(?: {6}-[ \t]+| {8})(?:&(?!&)[^ \t]+|\*[^ \t]+|!(?!=)[^ \t]+)[ \t]+'
        Message = 'YAML node properties and aliases are not supported on QA workflow steps or keys.'
    }
)
foreach ($unsupportedForm in $unsupportedYamlForms) {
    if ([regex]::IsMatch($workflow, $unsupportedForm.Pattern)) {
        throw $unsupportedForm.Message
    }
}

function Get-RunEntries {
    param([Parameter(Mandatory)][string] $Yaml)

    $matches = @(
        [regex]::Matches(
            $Yaml,
            '(?m)^(?<indent>[ \t]*)(?:-[ \t]+)?run[ \t]*:[ \t]*(?:(?<style>[>|])(?<chomp>[-+]?)[ \t]*|(?<inline>[^\r\n]*))[ \t]*\r?$') |
            ForEach-Object { $_ }
    )
    foreach ($match in $matches) {
        $style = $match.Groups['style'].Value
        $command = $match.Groups['inline'].Value.Trim()
        if (-not $match.Groups['style'].Success -and
            $command -match '^[>|!&*]') {
            throw 'Unsupported YAML run scalar syntax is not allowed by the QA workflow contract.'
        }
        if (-not $match.Groups['style'].Success) {
            $runIndent = $match.Groups['indent'].Value.Length
            $tail = $Yaml.Substring($match.Index + $match.Length)
            $tailLines = @($tail -split '\r?\n')
            if ($tailLines.Count -gt 0 -and $tailLines[0].Length -eq 0) {
                $tailLines = @($tailLines | Select-Object -Skip 1)
            }
            foreach ($line in $tailLines) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }
                $contentStart = $line.Length - $line.TrimStart().Length
                if ($contentStart -gt $runIndent) {
                    throw 'Inline YAML run scalar continuations are not supported by the QA workflow contract.'
                }
                break
            }
        }
        $hasBlankLine = $false
        $hasMoreIndentedLine = $false
        if ($match.Groups['style'].Success) {
            $runIndent = $match.Groups['indent'].Value.Length
            $block = $Yaml.Substring($match.Index + $match.Length)
            $blockLines = @($block -split '\r?\n')
            if ($blockLines.Count -gt 0 -and $blockLines[0].Length -eq 0) {
                $blockLines = @($blockLines | Select-Object -Skip 1)
            }

            $contentIndent = $null
            foreach ($line in $blockLines) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                $contentStart = $line.Length - $line.TrimStart().Length
                if ($contentStart -le $runIndent) {
                    break
                }
                $contentIndent = $contentStart
                break
            }

            $commandLines = [Collections.Generic.List[object]]::new()
            if ($null -ne $contentIndent) {
                foreach ($line in $blockLines) {
                    if ([string]::IsNullOrWhiteSpace($line)) {
                        $commandLines.Add($null)
                        continue
                    }

                    $contentStart = $line.Length - $line.TrimStart().Length
                    if ($contentStart -lt $contentIndent) {
                        break
                    }
                    if ($contentStart -gt $contentIndent) {
                        $hasMoreIndentedLine = $true
                    }
                    $commandLines.Add($line.Substring($contentIndent))
                }
            }

            while ($commandLines.Count -gt 0 -and
                $null -eq $commandLines[$commandLines.Count - 1]) {
                $commandLines.RemoveAt($commandLines.Count - 1)
            }
            $hasBlankLine = $commandLines.Contains($null)
            if ([string]::Equals(
                    $style,
                    '>',
                    [StringComparison]::Ordinal) -and
                -not $hasBlankLine -and
                -not $hasMoreIndentedLine) {
                $command = @($commandLines) -join ' '
            } else {
                $command = @(
                    $commandLines |
                        ForEach-Object { if ($null -eq $_) { '' } else { $_ } }
                ) -join "`n"
            }
        }

        [pscustomobject]@{
            Command = $command
            HasBlankLine = $hasBlankLine
            HasMoreIndentedLine = $hasMoreIndentedLine
            Index = $match.Index
            Style = $style
        }
    }
}

$allRunEntries = @(Get-RunEntries -Yaml $workflow)

$writeEvidenceCommand = @(
    'New-Item -ItemType Directory -Path QaEvidence -Force | Out-Null'
    '. ./trusted/scripts/PullRequestClassification.ps1'
    "Write-Base64Utf8File -Base64 `$env:BODY_BASE64 -Path 'QaEvidence/pr-body.md'"
    "Write-Base64Utf8File -Base64 `$env:FILES_BASE64 -Path 'QaEvidence/changed-files.txt'"
) -join "`n"
$evaluateEvidenceCommand = @(
    './trusted/scripts/New-QaEvidence.ps1 `'
    '  -PullRequestNumber $env:PR_NUMBER `'
    '  -PullRequestTitle $env:PR_TITLE `'
    '  -PullRequestBodyPath QaEvidence/pr-body.md `'
    '  -HeadSha $env:HEAD_SHA `'
    '  -TrustedKnowledgeRevision $env:TRUSTED_KNOWLEDGE_REVISION `'
    '  -TestResultsPath source/TestResults `'
    '  -OutputDirectory QaEvidence `'
    '  -ChangedFilesPath QaEvidence/changed-files.txt'
    '$result = Get-Content QaEvidence/qa-result.json -Raw | ConvertFrom-Json'
    '"metadata-digest=$($result.metadataDigest)" >> $env:GITHUB_OUTPUT'
) -join "`n"
$expectedRunEntries = @(
    @{
        Name = 'Restore'
        Command = 'dotnet restore --locked-mode'
        Styles = @('')
        WorkingDirectory = 'source'
        Shell = $null
    },
    @{
        Name = 'Build'
        Command = 'dotnet build --no-restore'
        Styles = @('')
        WorkingDirectory = 'source'
        Shell = $null
    },
    @{
        Name = 'Run independent QA tests'
        Command =
            'dotnet test AgenticHotelBooking.slnx ' +
            '--configuration Debug ' +
            '--no-build ' +
            '--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests. ' +
            '--logger trx ' +
            '--results-directory TestResults'
        Styles = @('', '>')
        WorkingDirectory = 'source'
        Shell = $null
    },
    @{
        Name = 'Write pull request evidence'
        Command = $writeEvidenceCommand
        Styles = @('|')
        WorkingDirectory = $null
        Shell = 'pwsh'
    },
    @{
        Name = 'Evaluate QA evidence'
        Command = $evaluateEvidenceCommand
        Styles = @('|')
        WorkingDirectory = $null
        Shell = 'pwsh'
    }
)
if ($allRunEntries.Count -ne $expectedRunEntries.Count) {
    throw "QA job contains $($allRunEntries.Count) run entries; expected exactly $($expectedRunEntries.Count)."
}

$stepsMarker = [regex]::Match($workflow, '(?m)^    steps:[ \t]*\r?$')
if (-not $stepsMarker.Success) {
    throw 'QA workflow must contain the canonical qa job steps mapping.'
}
$stepMarkers = @(
    [regex]::Matches($workflow, '(?m)^      -[ \t]+') |
        Where-Object { $_.Index -gt $stepsMarker.Index }
)
for ($index = 0; $index -lt $allRunEntries.Count; $index++) {
    $runEntry = $allRunEntries[$index]
    $expected = $expectedRunEntries[$index]
    $stepMarker = @(
        $stepMarkers |
            Where-Object { $_.Index -le $runEntry.Index } |
            Select-Object -Last 1
    )
    if ($stepMarker.Count -ne 1) {
        throw "Run entry $($index + 1) is not associated with a canonical QA step."
    }
    $nextStepMarker = @(
        $stepMarkers |
            Where-Object { $_.Index -gt $stepMarker[0].Index } |
            Select-Object -First 1
    )
    $stepEnd = if ($nextStepMarker.Count -eq 1) {
        $nextStepMarker[0].Index
    } else {
        $workflow.Length
    }
    $step = $workflow.Substring(
        $stepMarker[0].Index,
        $stepEnd - $stepMarker[0].Index)
    $nameMatch = [regex]::Match(
        $step,
        '(?m)^      -[ \t]+name:[ \t]+(?<name>[^\r\n]+?)[ \t]*\r?$')
    if (-not $nameMatch.Success -or
        -not [string]::Equals(
            $nameMatch.Groups['name'].Value,
            $expected.Name,
            [StringComparison]::Ordinal)) {
        throw "Run entry $($index + 1) must belong to step '$($expected.Name)'."
    }
    if (-not [string]::Equals(
            $runEntry.Command,
            $expected.Command,
            [StringComparison]::Ordinal)) {
        throw "Step '$($expected.Name)' does not contain its exact allowlisted command."
    }
    if (-not ($expected.Styles -ccontains $runEntry.Style)) {
        throw "Step '$($expected.Name)' uses unsupported YAML run scalar style '$($runEntry.Style)'."
    }

    $workingDirectories = @(
        [regex]::Matches(
            $step,
            '(?m)^        working-directory:[ \t]*(?<value>[^\r\n]+?)[ \t]*\r?$')
    )
    if ($null -eq $expected.WorkingDirectory) {
        if ($workingDirectories.Count -ne 0) {
            throw "Step '$($expected.Name)' must not set a working directory."
        }
    } elseif ($workingDirectories.Count -ne 1 -or
        -not [string]::Equals(
            $workingDirectories[0].Groups['value'].Value,
            $expected.WorkingDirectory,
            [StringComparison]::Ordinal)) {
        throw "Step '$($expected.Name)' must use working directory '$($expected.WorkingDirectory)'."
    }

    $shells = @(
        [regex]::Matches(
            $step,
            '(?m)^        shell:[ \t]*(?<value>[^\r\n]+?)[ \t]*\r?$')
    )
    if ($null -eq $expected.Shell) {
        if ($shells.Count -ne 0) {
            throw "Step '$($expected.Name)' must use the default shell."
        }
    } elseif ($shells.Count -ne 1 -or
        -not [string]::Equals(
            $shells[0].Groups['value'].Value,
            $expected.Shell,
            [StringComparison]::Ordinal)) {
        throw "Step '$($expected.Name)' must use shell '$($expected.Shell)'."
    }
    if ([regex]::IsMatch($step, '(?m)^        if:')) {
        throw "Run step '$($expected.Name)' must not be conditional."
    }
}

$stepMarkers = @(
    [regex]::Matches(
        $workflow,
        '(?m)^(?<indent>[ \t]*)-[ \t]+name:[ \t]+Run independent QA tests[ \t]*\r?$') |
        ForEach-Object { $_ }
)
if ($stepMarkers.Count -ne 1) {
    throw 'QA workflow must contain exactly one independent test step.'
}
$stepMarker = $stepMarkers[0]
$stepStart = $stepMarker.Index
$stepIndent = $stepMarker.Groups['indent'].Value

$nextStepRegex = [regex]::new(
    "(?m)^$([regex]::Escape($stepIndent))-[ \t]+")
$nextStepMatch = $nextStepRegex.Match(
    $workflow,
    $stepStart + $stepMarker.Length)
if (-not $nextStepMatch.Success) {
    $nextStep = $workflow.Length
} else {
    $nextStep = $nextStepMatch.Index
}

$step = $workflow.Substring($stepStart, $nextStep - $stepStart)
$workingDirectories = @(
    [regex]::Matches(
        $step,
        '(?m)^[ \t]*working-directory:[ \t]*(?<path>[^\r\n]+?)[ \t]*\r?$') |
        ForEach-Object { $_ }
)
if ($workingDirectories.Count -ne 1 -or
    -not [string]::Equals(
        $workingDirectories[0].Groups['path'].Value,
        'source',
        [StringComparison]::Ordinal)) {
    throw 'Independent QA tests must run from exactly the source working directory.'
}

$stepRunEntries = @(
    $allRunEntries |
        Where-Object { $_.Index -ge $stepStart -and $_.Index -lt $nextStep }
)
if ($stepRunEntries.Count -ne 1) {
    throw 'Independent QA tests must contain exactly one run command.'
}

$runEntry = $stepRunEntries[0]
if ($runEntry.Style.Length -gt 0 -and
    -not [string]::Equals(
        $runEntry.Style,
        '>',
        [StringComparison]::Ordinal)) {
    throw 'Independent QA multiline tests must use a folded YAML run block.'
}
if ($runEntry.HasBlankLine -or $runEntry.HasMoreIndentedLine) {
    throw 'Independent QA folded run block must contain only equally indented, nonblank command lines.'
}
$command = $runEntry.Command

$expectedCommand =
    'dotnet test AgenticHotelBooking.slnx ' +
    '--configuration Debug ' +
    '--no-build ' +
    '--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests. ' +
    '--logger trx ' +
    '--results-directory TestResults'
if (-not [string]::Equals(
        $command,
        $expectedCommand,
        [StringComparison]::Ordinal)) {
    throw "Independent QA test command is '$command'; expected exactly '$expectedCommand'."
}

Write-Host 'QA test selection contract passed.'
