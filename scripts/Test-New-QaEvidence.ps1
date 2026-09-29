[CmdletBinding()]
param(
    [string] $WorkflowPath =
        (Join-Path $PSScriptRoot '../.github/workflows/qa-evidence.yml'),

    [switch] $SkipSemanticMatrix,

    [string] $OraclePackagePath
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PullRequestClassification.ps1"

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        $Actual,

        [Parameter(Mandatory)]
        [AllowNull()]
        $Expected,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Invoke-IsolatedYamlOracle {
    param(
        [Parameter(Mandatory)]
        [string] $Script,

        [Parameter(Mandatory)]
        [string] $OraclePackagePath,

        [string[]] $Arguments = @()
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = $Script |
            python -I -S - $OraclePackagePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-Evidence {
    param(
        [Parameter(Mandatory)]
        [string] $Body,

        [Parameter(Mandatory)]
        [string] $CaseName,

        [string] $Title = 'AB#999 evidence change',

        [string[]] $ChangedFiles = @('src/Api/Program.cs'),

        [string] $Counters = 'total="2" executed="2" passed="2" failed="0"'
    )

    $casePath = Join-Path $testRoot $CaseName
    $resultsPath = Join-Path $casePath 'TestResults'
    $outputPath = Join-Path $casePath 'QaEvidence'
    New-Item -ItemType Directory -Path $resultsPath -Force | Out-Null
    $body | Set-Content (Join-Path $casePath 'body.md')
    $ChangedFiles | Set-Content (Join-Path $casePath 'changed-files.txt')
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun>
  <ResultSummary>
    <Counters $Counters />
  </ResultSummary>
</TestRun>
"@ | Set-Content (Join-Path $resultsPath 'results.trx')

    $failure = $null
    try {
        & "$PSScriptRoot/New-QaEvidence.ps1" `
            -PullRequestNumber 9 `
            -PullRequestTitle $Title `
            -PullRequestBodyPath (Join-Path $casePath 'body.md') `
            -HeadSha 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' `
            -TrustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' `
            -TestResultsPath $resultsPath `
            -OutputDirectory $outputPath `
            -ChangedFilesPath (Join-Path $casePath 'changed-files.txt')
    }
    catch {
        $failure = $_
    }

    [pscustomobject]@{
        Failure = $failure
        Result = Get-Content (Join-Path $outputPath 'qa-result.json') -Raw | ConvertFrom-Json
    }
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "New-QaEvidence-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $qaWorkflow = [IO.File]::ReadAllText(
        (Resolve-Path -LiteralPath $WorkflowPath))
    $workflowNewLine = if ($qaWorkflow.Contains("`r`n")) { "`r`n" } else { "`n" }
    $releaseWorkflow = [IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot '../.github/workflows/release-proposal.yml'))
    $productionWorkflow = [IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot '../.github/workflows/deploy-production.yml'))
    $prValidationWorkflow = [IO.File]::ReadAllText(
        (Join-Path $PSScriptRoot '../.github/workflows/pr-validation.yml'))
    $validationRequirementsPath =
        Join-Path $PSScriptRoot 'requirements-validation.txt'
    $validationRequirements = [IO.File]::ReadAllText(
        $validationRequirementsPath).Trim()
    Assert-Equal `
        $validationRequirements `
        @'
PyYAML==6.0.3 \
    --hash=sha256:ba1cc08a7ccde2d2ec775841541641e4548226580ab850948cbfda66a1befcdc \
    --hash=sha256:5fcd34e47f6e0b794d17de1b4ff496c00986e1c83f7ab2fb8fcfe9616ff7477b \
    --hash=sha256:0f29edc409a6392443abf94b9cf89ce99889a1dd5376d94316ae5145dfedd5d6 \
    --hash=sha256:79005a0d97d5ddabfeeea4cf676af11e647e41d81c9a7722a193022accdb6b7c \
    --hash=sha256:c458b6d084f9b935061bc36216e8a69a7e293a2f1e68bf956dcd9e6cbcd143f5 \
    --hash=sha256:4a2e8cebe2ff6ab7d1050ecd59c25d4c8bd7e6f400f5f82b96557ac0abafd0ac
'@.Trim() `
        'QA semantic-oracle dependency must be exactly version- and hash-locked.'
    Assert-Equal (
        $prValidationWorkflow.Contains('Install QA validation dependencies')
    ) $false 'PR validation must not require a workflow-only oracle installation step.'

    $ownsOraclePackagePath = [string]::IsNullOrWhiteSpace($OraclePackagePath)
    $oraclePackagePath = if ($ownsOraclePackagePath) {
        Join-Path $testRoot 'semantic-oracle-packages'
    } else {
        $OraclePackagePath
    }
    if ($ownsOraclePackagePath) {
        $installOutput = & python -m pip install `
            --disable-pip-version-check `
            --no-deps `
            --only-binary=:all: `
            --require-hashes `
            --target $oraclePackagePath `
            -r $validationRequirementsPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to provision the isolated QA semantic oracle: $installOutput"
        }
        $oracleVerification = Invoke-IsolatedYamlOracle `
            -OraclePackagePath $oraclePackagePath `
            -Script @'
import json
import pathlib
import sys

oracle_root = pathlib.Path(sys.argv[1]).resolve()
sys.path.insert(0, str(oracle_root))
import yaml

module_path = pathlib.Path(yaml.__file__).resolve()
if oracle_root not in module_path.parents:
    raise SystemExit(f"yaml imported from outside isolated target: {module_path}")
print(json.dumps({"version": yaml.__version__, "path": str(module_path)}))
'@
        Assert-Equal `
            $oracleVerification.ExitCode `
            0 `
            "The isolated QA semantic oracle must import without ambient packages: $($oracleVerification.Output)"
        $oracleIdentity = $oracleVerification.Output |
            Select-Object -Last 1 |
            ConvertFrom-Json
        Assert-Equal `
            $oracleIdentity.version `
            '6.0.3' `
            'The isolated QA semantic oracle must use the pinned PyYAML version.'
        Write-Host "Provisioned isolated PyYAML $($oracleIdentity.version) at '$($oracleIdentity.path)' with ambient site packages disabled."
    }
    Assert-Equal (
        $qaWorkflow.Contains(
            'name: qa-evidence-${{ steps.context.outputs.head-sha }}-${{ steps.evidence.outputs.metadata-digest }}')
    ) $true 'QA upload must include the metadata digest in its artifact identity.'
    Assert-Equal (
        $releaseWorkflow.Contains('qaMetadataDigest = $env:QA_METADATA_DIGEST')
    ) $true 'Release evidence must preserve the QA metadata digest.'
    Assert-Equal (
        $productionWorkflow.Contains(
            'artifact.name === `qa-evidence-${process.env.COMMIT_SHA}-${evidence.qaMetadataDigest}`')
    ) $true 'Production preflight must require the digest-bound QA artifact.'
    Assert-Equal (
        $qaWorkflow.Contains(
            "Write-Base64Utf8File -Base64 `$env:BODY_BASE64 -Path 'QaEvidence/pr-body.md'")
    ) $true 'QA workflow must use the tested body serialization helper.'
    & "$PSScriptRoot/Assert-QaTestSelection.ps1" `
        -WorkflowPath $WorkflowPath `
        -OraclePackagePath $oraclePackagePath

    $shellPath = (Get-Process -Id $PID).Path
    $selectionMutations = @(
        @{
            Name = 'overbroad-filter'
            Old = '--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests.'
            New = '--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests.&FullyQualifiedName!~SqlManagedIdentityBootstrapperTests'
        },
        @{
            Name = 'near-name-substring-filter'
            Old = '--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests.'
            New = '--filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests'
        },
        @{
            Name = 'unit-tests-only'
            Old = 'dotnet test AgenticHotelBooking.slnx'
            New = 'dotnet test tests/UnitTests/AgenticHotelBooking.UnitTests.csproj'
        },
        @{
            Name = 'integration-tests-only'
            Old = 'dotnet test AgenticHotelBooking.slnx'
            New = 'dotnet test tests/IntegrationTests/AgenticHotelBooking.IntegrationTests.csproj'
        },
        @{
            Name = 'wrong-solution'
            Old = 'dotnet test AgenticHotelBooking.slnx'
            New = 'dotnet test Other.slnx'
        },
        @{
            Name = 'wrong-configuration'
            Old = '          --configuration Debug'
            New = '          --configuration Release'
        },
        @{
            Name = 'trusted-checkout-ref'
            Old = '          ref: ${{ github.event.repository.default_branch }}'
            New = '          ref: ${{ steps.context.outputs.head-sha }}'
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = 'Check out trusted QA policy'
            OracleKey = 'ref'
            OracleExpected = '${{ steps.context.outputs.head-sha }}'
        },
        @{
            Name = 'action-extra-path'
            Old = '          dotnet-version: 10.0.x'
            New = "          dotnet-version: 10.0.x`n          path: unexpected"
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = '-'
            OracleKey = 'path'
            OracleExpected = 'unexpected'
        },
        @{
            Name = 'checkout-enable-credentials'
            Old = "          path: trusted${workflowNewLine}          persist-credentials: false"
            New = "          path: trusted${workflowNewLine}          persist-credentials: true"
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = 'Check out trusted QA policy'
            OracleKey = 'persist-credentials'
            OracleExpected = 'True'
        },
        @{
            Name = 'checkout-fetch-depth'
            Old = "          path: trusted${workflowNewLine}          persist-credentials: false"
            New = "          path: trusted${workflowNewLine}          persist-credentials: false${workflowNewLine}          fetch-depth: 0"
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = 'Check out trusted QA policy'
            OracleKey = 'fetch-depth'
            OracleExpected = '0'
        },
        @{
            Name = 'action-extra-with'
            Old = '          inlineScript: az bicep build --file source/infra/main.bicep'
            New = "          inlineScript: az bicep build --file source/infra/main.bicep`n          unexpected: true"
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = 'Build Bicep'
            OracleKey = 'unexpected'
            OracleExpected = 'True'
        },
        @{
            Name = 'step-extra-env'
            Old = "      - name: Restore${workflowNewLine}        working-directory: source"
            New = "      - name: Restore${workflowNewLine}        env:${workflowNewLine}          DOTNET_ROOT: unexpected${workflowNewLine}        working-directory: source"
            SemanticContextMutation = $true
            OracleScope = 'step-env'
            OracleStepName = 'Restore'
            OracleKey = 'DOTNET_ROOT'
            OracleExpected = 'unexpected'
        },
        @{
            Name = 'aliased-checkout-input'
            Old = '          ref: ${{ github.event.repository.default_branch }}'
            New = "          ref: &trusted-ref `${{ github.event.repository.default_branch }}`n          x-ref: *trusted-ref"
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = 'Check out trusted QA policy'
            OracleKey = 'x-ref'
            OracleExpected = '${{ github.event.repository.default_branch }}'
        },
        @{
            Name = 'merged-checkout-input'
            Old = '          ref: ${{ github.event.repository.default_branch }}'
            New = "          <<: { fetch-depth: 0 }`n          ref: `${{ github.event.repository.default_branch }}"
            SemanticContextMutation = $true
            OracleScope = 'step-with'
            OracleStepName = 'Check out trusted QA policy'
            OracleKey = 'fetch-depth'
            OracleExpected = '0'
        },
        @{
            Name = 'workflow-permission-write'
            Old = '  contents: read'
            New = '  contents: write'
            SemanticContextMutation = $true
            OracleScope = 'workflow-permission'
            OracleKey = 'contents'
            OracleExpected = 'write'
        },
        @{
            Name = 'duplicate-permissions-contents'
            Old = "permissions:${workflowNewLine}  actions: read"
            New = "permissions:${workflowNewLine}  contents: write${workflowNewLine}  actions: read"
            DuplicateKey = 'contents'
        },
        @{
            Name = 'duplicate-checkout-with'
            Old = "      - name: Check out trusted QA policy${workflowNewLine}        uses: actions/checkout@v4${workflowNewLine}        with:"
            New = "      - name: Check out trusted QA policy${workflowNewLine}        uses: actions/checkout@v4${workflowNewLine}        with: { ref: refs/heads/untrusted }${workflowNewLine}        with:"
            DuplicateKey = 'with'
        },
        @{
            Name = 'workflow-extra-env'
            Old = 'concurrency:'
            New = "env:`n  SOURCE: unexpected`n`nconcurrency:"
            SemanticContextMutation = $true
            OracleScope = 'workflow-env'
            OracleKey = 'SOURCE'
            OracleExpected = 'unexpected'
        },
        @{
            Name = 'job-condition-change'
            Old = "      github.event_name == 'workflow_dispatch'"
            New = "      false && github.event_name == 'workflow_dispatch'"
            SemanticContextMutation = $true
            OracleScope = 'job-if-prefix'
            OracleKey = '-'
            OracleExpected = 'false &&'
        },
        @{
            Name = 'workflow-default-shell'
            Old = 'jobs:'
            New = "defaults:`n  run:`n    shell: pwsh`n`njobs:"
        },
        @{
            Name = 'workflow-default-working-directory'
            Old = 'jobs:'
            New = "defaults:`n  run:`n    working-directory: trusted`n`njobs:"
        },
        @{
            Name = 'job-default-shell'
            Old = '    name: Independent QA evidence gate'
            New = "    defaults:`n      run:`n        shell: pwsh`n    name: Independent QA evidence gate"
        },
        @{
            Name = 'job-default-working-directory'
            Old = '    name: Independent QA evidence gate'
            New = "    defaults:`n      run:`n        working-directory: trusted`n    name: Independent QA evidence gate"
        },
        @{
            Name = 'step-shell-override'
            Old = '      - name: Restore'
            New = "      - name: Restore`n        shell: pwsh"
        },
        @{
            Name = 'step-working-directory-override'
            Old = '      - name: Evaluate QA evidence'
            New = "      - name: Evaluate QA evidence`n        working-directory: source"
        },
        @{
            Name = 'quoted-test-executable'
            Old = '          dotnet test AgenticHotelBooking.slnx'
            New = "          & 'dotnet' 'test' AgenticHotelBooking.slnx"
        },
        @{
            Name = 'appended-restore-command'
            Old = '        run: dotnet restore --locked-mode'
            New = '        run: dotnet restore --locked-mode; echo unexpected'
        },
        @{
            Name = 'continued-restore-command'
            Old = '        run: dotnet restore --locked-mode'
            New = "        run: dotnet restore --locked-mode`n          ; echo unexpected"
            SemanticAlteredRun = $true
        },
        @{
            Name = 'appended-evidence-command'
            Old = "          New-Item -ItemType Directory -Path QaEvidence -Force | Out-Null"
            New = "          New-Item -ItemType Directory -Path QaEvidence -Force | Out-Null`n          echo unexpected"
        },
        @{
            Name = 'extra-test-command'
            Old = '          --results-directory TestResults'
            New = "          --results-directory TestResults`n          ; dotnet test tests/UnitTests/AgenticHotelBooking.UnitTests.csproj"
        },
        @{
            Name = 'missing-test-command'
            Old = '          dotnet test AgenticHotelBooking.slnx'
            New = '          echo tests omitted'
        },
        @{
            Name = 'separate-step-test-command'
            Old = '      - name: Build Bicep'
            New = "      - name: Extra test command`n        working-directory: source`n        run: dotnet test tests/UnitTests/AgenticHotelBooking.UnitTests.csproj`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'unnamed-inline-test-command'
            Old = '      - name: Build Bicep'
            New = "      - run: dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'conditional-unnamed-test-command'
            Old = '      - name: Build Bicep'
            New = "      - if: false`n        run: dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'quoted-extra-test-command'
            Old = '      - name: Build Bicep'
            New = '      - run: command "dotnet" test Other.slnx' +
                "`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'double-quoted-run-key'
            Old = '      - name: Build Bicep'
            New = "      - `"run`": dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'single-quoted-run-key'
            Old = '      - name: Build Bicep'
            New = "      - 'run': dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'escaped-run-key'
            Old = '      - name: Build Bicep'
            New = "      - `"r\u0075n`": `"dotnet\u0020test Other.slnx`"`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'space-before-run-colon'
            Old = '      - name: Build Bicep'
            New = "      - run : dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'double-quoted-inline-scalar'
            Old = '      - name: Build Bicep'
            New = "      - run: `"dotnet\u0020test Other.slnx`"`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'single-quoted-inline-scalar'
            Old = '      - name: Build Bicep'
            New = "      - run: 'dotnet test Other.slnx'`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'flow-style-run-mapping'
            Old = '      - name: Build Bicep'
            New = "      - { run: `"dotnet test Other.slnx`" }`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'explicit-run-key'
            Old = '      - name: Build Bicep'
            New = "      - ? run`n        : dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'nested-explicit-run-key'
            Old = '      - name: Build Bicep'
            New = "      - name: Extra explicit run`n        ? run`n        : `"dotnet\u0020test Other.slnx`"`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'anchored-run-key'
            Old = '      - name: Build Bicep'
            New = "      - &extra run: `"dotnet\u0020test Other.slnx`"`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'hyphen-anchored-run-key'
            Old = '      - name: Build Bicep'
            New = "      - &-extra run: `"dotnet\u0020test Other.slnx`"`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'hyphen-anchored-run-key'
            Old = '      - name: Build Bicep'
            New = "      - &-extra run: `"dotnet\u0020test Other.slnx`"`n`n      - name: Build Bicep"
        },
        @{
            Name = 'nested-anchored-run-key'
            Old = '      - name: Build Bicep'
            New = "      - name: Extra anchored run`n        &extra run: dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'tagged-run-key'
            Old = '      - name: Build Bicep'
            New = "      - !!str run: dotnet test Other.slnx`n`n      - name: Build Bicep"
            SemanticExtraRun = $true
        },
        @{
            Name = 'explicit-block-indent'
            Old = '      - name: Build Bicep'
            New = "      - run: >2-`n          dotnet test Other.slnx`n`n      - name: Build Bicep"
        },
        @{
            Name = 'folded-blank-line'
            Old = '          --no-build'
            New = "`n          --no-build"
        },
        @{
            Name = 'folded-more-indented-line'
            Old = '          --no-build'
            New = '            --no-build'
        }
    )
    $whitespaceKeyMutations = @(
        @{
            Name = 'whitespace-workflow-defaults-key'
            Old = 'jobs:'
            New = "defaults :`n  run :`n    shell : pwsh`n`njobs:"
            SemanticWhitespaceKey = $true
            OracleScope = 'workflow-default'
            OracleKey = 'shell'
            OracleExpected = 'pwsh'
        },
        @{
            Name = 'whitespace-job-defaults-key'
            Old = '    name: Independent QA evidence gate'
            New = "    defaults :`n      run :`n        working-directory : trusted`n    name: Independent QA evidence gate"
            SemanticWhitespaceKey = $true
            OracleScope = 'job-default'
            OracleKey = 'working-directory'
            OracleExpected = 'trusted'
        },
        @{
            Name = 'whitespace-step-shell-key'
            Old = '      - name: Restore'
            New = "      - name: Restore`n        shell : pwsh"
            SemanticWhitespaceKey = $true
            OracleScope = 'step'
            OracleStepName = 'Restore'
            OracleKey = 'shell'
            OracleExpected = 'pwsh'
        },
        @{
            Name = 'whitespace-step-working-directory-key'
            Old = '      - name: Evaluate QA evidence'
            New = "      - name: Evaluate QA evidence`n        working-directory : source"
            SemanticWhitespaceKey = $true
            OracleScope = 'step'
            OracleStepName = 'Evaluate QA evidence'
            OracleKey = 'working-directory'
            OracleExpected = 'source'
        },
        @{
            Name = 'whitespace-step-if-key'
            Old = '      - name: Restore'
            New = "      - name: Restore`n        if : false"
            SemanticWhitespaceKey = $true
            OracleScope = 'step'
            OracleStepName = 'Restore'
            OracleKey = 'if'
            OracleExpected = 'False'
        },
        @{
            Name = 'whitespace-step-run-key'
            Old = '        run: dotnet restore --locked-mode'
            New = '        run : dotnet restore --locked-mode; echo unexpected'
            SemanticWhitespaceKey = $true
            OracleScope = 'step'
            OracleStepName = 'Restore'
            OracleKey = 'run'
            OracleExpected = 'dotnet restore --locked-mode; echo unexpected'
        }
    )
    $selectionMutations += $whitespaceKeyMutations
    $executionContextMutations = @(
        @{
            Name = 'flow-workflow-default-shell'
            Old = 'jobs:'
            New = "defaults: { run: { shell: pwsh } }`n`njobs:"
            SemanticContextMutation = $true
            OracleScope = 'workflow-default'
            OracleKey = 'shell'
            OracleExpected = 'pwsh'
        },
        @{
            Name = 'flow-job-default-shell'
            Old = '    name: Independent QA evidence gate'
            New = "    defaults: { run: { shell: pwsh } }`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-default'
            OracleKey = 'shell'
            OracleExpected = 'pwsh'
        },
        @{
            Name = 'flow-workflow-default-working-directory'
            Old = 'jobs:'
            New = "defaults: { run: { working-directory: trusted } }`n`njobs:"
            SemanticContextMutation = $true
            OracleScope = 'workflow-default'
            OracleKey = 'working-directory'
            OracleExpected = 'trusted'
        },
        @{
            Name = 'flow-job-default-working-directory'
            Old = '    name: Independent QA evidence gate'
            New = "    defaults: { run: { working-directory: trusted } }`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-default'
            OracleKey = 'working-directory'
            OracleExpected = 'trusted'
        },
        @{
            Name = 'windows-job-runner'
            Old = '    runs-on: ubuntu-24.04'
            New = '    runs-on: windows-latest'
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'runs-on'
            OracleExpected = 'windows-latest'
        },
        @{
            Name = 'macos-job-runner'
            Old = '    runs-on: ubuntu-24.04'
            New = '    runs-on: macos-latest'
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'runs-on'
            OracleExpected = 'macos-latest'
        },
        @{
            Name = 'job-container'
            Old = '    runs-on: ubuntu-24.04'
            New = "    runs-on: ubuntu-24.04`n    container: ubuntu:24.04"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'combined-job-context'
            Old = '    name: Independent QA evidence gate'
            New = "    name: Independent QA evidence gate`n    defaults: { run: { shell: pwsh, working-directory: trusted } }`n    container: ubuntu:24.04"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'explicit-job-container-key'
            Old = '    name: Independent QA evidence gate'
            New = "    ? container`n    : ubuntu:24.04`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'quoted-job-container-key'
            Old = '    name: Independent QA evidence gate'
            New = "    `"container`": ubuntu:24.04`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'whitespace-job-container-key'
            Old = '    name: Independent QA evidence gate'
            New = "    container : ubuntu:24.04`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'anchored-job-container-key'
            Old = '    name: Independent QA evidence gate'
            New = "    &ctx container: ubuntu:24.04`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'tagged-job-container-key'
            Old = '    name: Independent QA evidence gate'
            New = "    !!str container: ubuntu:24.04`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'aliased-job-container-value'
            Old = '    name: Independent QA evidence gate'
            New = "    container: &image ubuntu:24.04`n    x-image: *image`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        },
        @{
            Name = 'merged-job-container'
            Old = '    name: Independent QA evidence gate'
            New = "    <<: { container: ubuntu:24.04 }`n    name: Independent QA evidence gate"
            SemanticContextMutation = $true
            OracleScope = 'job-key'
            OracleKey = 'container'
            OracleExpected = 'ubuntu:24.04'
        }
    )
    $selectionMutations += $executionContextMutations
    foreach ($mutation in $selectionMutations) {
        $matches = ([regex]::Matches(
                $qaWorkflow,
                [regex]::Escape($mutation.Old))).Count
        Assert-Equal $matches 1 "QA selection mutant '$($mutation.Name)' must match once."
        $mutatedWorkflowPath =
            Join-Path $testRoot "qa-evidence-$($mutation.Name).yml"
        $qaWorkflow.Replace(
            $mutation.Old,
            $mutation.New
        ) | Set-Content -LiteralPath $mutatedWorkflowPath
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & $shellPath -NoProfile `
                -File "$PSScriptRoot/Assert-QaTestSelection.ps1" `
                -WorkflowPath $mutatedWorkflowPath `
                -OraclePackagePath $oraclePackagePath 2>$null | Out-Null
            $mutationExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        Assert-Equal `
            $mutationExitCode `
            1 `
            "QA selection mutant '$($mutation.Name)' must fail."
        Write-Host "QA selection mutant '$($mutation.Name)' was rejected with exit code $mutationExitCode."

        if ($mutation.DuplicateKey -and -not $SkipSemanticMatrix) {
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $duplicateKeyOutput = & python `
                    -I `
                    -S `
                    "$PSScriptRoot/assert_qa_test_selection.py" `
                    $oraclePackagePath `
                    $mutatedWorkflowPath 2>&1
                $duplicateKeyExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
            Assert-Equal `
                $duplicateKeyExitCode `
                1 `
                "Semantic parser must reject duplicate key '$($mutation.DuplicateKey)'."
            Assert-Equal `
                (($duplicateKeyOutput -join "`n").Contains(
                    "Duplicate YAML mapping key: $($mutation.DuplicateKey)")) `
                $true `
                "Semantic parser must identify duplicate key '$($mutation.DuplicateKey)'."
            Write-Host "Semantic YAML duplicate key '$($mutation.DuplicateKey)' was rejected."
        }

        if ($mutation.SemanticExtraRun -and -not $SkipSemanticMatrix) {
            $oracleScript = @'
import pathlib
import sys

oracle_root = pathlib.Path(sys.argv[1]).resolve()
sys.path.insert(0, str(oracle_root))
import yaml

with open(sys.argv[2], encoding="utf-8") as stream:
    workflow = yaml.safe_load(stream)
steps = workflow["jobs"]["qa"]["steps"]
runs = [
    step["run"]
    for step in steps
    if isinstance(step, dict) and isinstance(step.get("run"), str)
]
extra = [
    run
    for run in runs
    if "Other.slnx" in run or "tests/UnitTests" in run
]
if len(runs) != 6 or len(extra) != 1:
    raise SystemExit(
        f"expected six decoded run entries and one extra test command; "
        f"got runs={len(runs)}, extra={len(extra)}"
    )
'@
            $oracleResult = Invoke-IsolatedYamlOracle `
                -OraclePackagePath $oraclePackagePath `
                -Script $oracleScript `
                -Arguments $mutatedWorkflowPath
            Assert-Equal `
                $oracleResult.ExitCode `
                0 `
                "Semantic YAML oracle rejected '$($mutation.Name)': $($oracleResult.Output)"

            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & $shellPath -NoProfile `
                    -File $PSCommandPath `
                    -WorkflowPath $mutatedWorkflowPath `
                    -SkipSemanticMatrix `
                    -OraclePackagePath $oraclePackagePath 2>$null | Out-Null
                $fullSelfTestExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
            Assert-Equal `
                $fullSelfTestExitCode `
                1 `
                "Full QA evidence self-test must reject semantic mutant '$($mutation.Name)'."
            Write-Host "Semantic YAML mutant '$($mutation.Name)' decoded to an extra run and was rejected by both gates."
        }
        if ($mutation.SemanticAlteredRun -and -not $SkipSemanticMatrix) {
            $continuationOracle = @'
import pathlib
import sys

oracle_root = pathlib.Path(sys.argv[1]).resolve()
sys.path.insert(0, str(oracle_root))
import yaml

with open(sys.argv[2], encoding="utf-8") as stream:
    workflow = yaml.safe_load(stream)
steps = workflow["jobs"]["qa"]["steps"]
restore = next(step for step in steps if step.get("name") == "Restore")
if restore["run"] != "dotnet restore --locked-mode ; echo unexpected":
    raise SystemExit(f"unexpected decoded restore command: {restore['run']!r}")
'@
            $oracleResult = Invoke-IsolatedYamlOracle `
                -OraclePackagePath $oraclePackagePath `
                -Script $continuationOracle `
                -Arguments $mutatedWorkflowPath
            Assert-Equal `
                $oracleResult.ExitCode `
                0 `
                "Semantic continuation oracle rejected '$($mutation.Name)': $($oracleResult.Output)"

            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & $shellPath -NoProfile `
                    -File $PSCommandPath `
                    -WorkflowPath $mutatedWorkflowPath `
                    -SkipSemanticMatrix `
                    -OraclePackagePath $oraclePackagePath 2>$null | Out-Null
                $fullSelfTestExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
            Assert-Equal `
                $fullSelfTestExitCode `
                1 `
                "Full QA evidence self-test must reject semantic mutant '$($mutation.Name)'."
            Write-Host "Semantic YAML continuation '$($mutation.Name)' altered an allowlisted command and was rejected by both gates."
        }
        if (($mutation.SemanticWhitespaceKey -or
                $mutation.SemanticContextMutation) -and
            -not $SkipSemanticMatrix) {
            $whitespaceKeyOracle = @'
import pathlib
import sys

oracle_root = pathlib.Path(sys.argv[1]).resolve()
sys.path.insert(0, str(oracle_root))
import yaml

with open(sys.argv[2], encoding="utf-8") as stream:
    workflow = yaml.safe_load(stream)
scope, key, step_name, expected = sys.argv[3:7]
if scope == "workflow-default":
    actual = workflow["defaults"]["run"][key]
elif scope == "workflow-permission":
    actual = workflow["permissions"][key]
elif scope == "workflow-env":
    actual = workflow["env"][key]
elif scope == "job-default":
    actual = workflow["jobs"]["qa"]["defaults"]["run"][key]
elif scope == "job-key":
    actual = workflow["jobs"]["qa"][key]
elif scope == "job-if-prefix":
    actual = workflow["jobs"]["qa"]["if"]
    if not actual.startswith(expected):
        raise SystemExit(
            f"unexpected decoded job condition: expected prefix {expected!r}, "
            f"got {actual!r}"
        )
    raise SystemExit(0)
elif scope in {"step", "step-with", "step-env"}:
    step = next(
        item
        for item in workflow["jobs"]["qa"]["steps"]
        if (
            item.get("uses") == "actions/setup-dotnet@v4"
            if step_name == "-"
            else item.get("name") == step_name
        )
    )
    if scope == "step-with":
        actual = step["with"][key]
    elif scope == "step-env":
        actual = step["env"][key]
    else:
        actual = step[key]
else:
    raise SystemExit(f"unsupported semantic scope: {scope}")
if str(actual) != expected:
    raise SystemExit(
        f"unexpected decoded value for {scope}.{key}: "
        f"expected {expected!r}, got {actual!r}"
    )
'@
            $oracleResult = Invoke-IsolatedYamlOracle `
                -OraclePackagePath $oraclePackagePath `
                -Script $whitespaceKeyOracle `
                -Arguments @(
                    $mutatedWorkflowPath,
                    $mutation.OracleScope,
                    $mutation.OracleKey,
                    $(if ($null -eq $mutation.OracleStepName) {
                            '-'
                        } else {
                            $mutation.OracleStepName
                        }),
                    $mutation.OracleExpected)
            Assert-Equal `
                $oracleResult.ExitCode `
                0 `
                "Semantic whitespace-key oracle rejected '$($mutation.Name)': $($oracleResult.Output)"

            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                & $shellPath -NoProfile `
                    -File $PSCommandPath `
                    -WorkflowPath $mutatedWorkflowPath `
                    -SkipSemanticMatrix `
                    -OraclePackagePath $oraclePackagePath 2>$null | Out-Null
                $fullSelfTestExitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
            Assert-Equal `
                $fullSelfTestExitCode `
                1 `
                "Full QA evidence self-test must reject semantic whitespace-key mutant '$($mutation.Name)'."
            Write-Host "Semantic YAML context mutant '$($mutation.Name)' decoded to altered workflow behavior and was rejected by both gates."
        }
    }

    $inlineWorkflowPath = Join-Path $testRoot 'qa-evidence-inline-run.yml'
    $foldedRunPattern =
        '(?m)^        run: >-\r?\n' +
        '          dotnet test AgenticHotelBooking\.slnx\r?\n' +
        '          --configuration Debug\r?\n' +
        '          --no-build\r?\n' +
        '          --filter FullyQualifiedName!~AgenticHotelBooking\.IntegrationTests\.SqlManagedIdentityBootstrapperSqlServerTests\.\r?\n' +
        '          --logger trx\r?\n' +
        '          --results-directory TestResults\r?$'
    $foldedRunRegex = [regex]::new($foldedRunPattern)
    Assert-Equal `
        $foldedRunRegex.Matches($qaWorkflow).Count `
        1 `
        'The valid inline-format fixture must replace exactly one run block.'
    $inlineWorkflow = $foldedRunRegex.Replace(
        $qaWorkflow,
        '        run: dotnet test AgenticHotelBooking.slnx --configuration Debug --no-build --filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests. --logger trx --results-directory TestResults',
        1)
    $inlineWorkflow | Set-Content -LiteralPath $inlineWorkflowPath
    & "$PSScriptRoot/Assert-QaTestSelection.ps1" `
        -WorkflowPath $inlineWorkflowPath `
        -OraclePackagePath $oraclePackagePath

    $literalWorkflowPath = Join-Path $testRoot 'qa-evidence-literal-run.yml'
    $literalWorkflow = $foldedRunRegex.Replace(
        $qaWorkflow,
        @'
        run: |-
          dotnet test AgenticHotelBooking.slnx
          --configuration Debug
          --no-build
          --filter FullyQualifiedName!~AgenticHotelBooking.IntegrationTests.SqlManagedIdentityBootstrapperSqlServerTests.
          --logger trx
          --results-directory TestResults
'@,
        1)
    $literalWorkflow | Set-Content -LiteralPath $literalWorkflowPath
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $shellPath -NoProfile `
            -File "$PSScriptRoot/Assert-QaTestSelection.ps1" `
            -WorkflowPath $literalWorkflowPath `
            -OraclePackagePath $oraclePackagePath 2>$null | Out-Null
        $literalExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    Assert-Equal $literalExitCode 1 'A literal multiline QA command must fail.'
    Write-Host "QA selection mutant 'literal-run-block' was rejected with exit code $literalExitCode."

    $rawBody = "- Azure Boards: N/A`r`n- Platform change: true`r`nCaf$([char]0x00E9)"
    $serializedBodyPath = Join-Path $testRoot 'serialized-body.md'
    $bodyBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($rawBody))
    Write-Base64Utf8File -Base64 $bodyBase64 -Path $serializedBodyPath
    $serializedBody = [IO.File]::ReadAllText($serializedBodyPath)
    Assert-Equal $serializedBody $rawBody 'Workflow serialization must preserve PR body bytes.'
    Assert-Equal `
        (Get-PullRequestMetadataDigest -Title 'Harden platform' -Body $serializedBody) `
        (Get-PullRequestMetadataDigest -Title 'Harden platform' -Body $rawBody) `
        'Producer and consumer metadata digests must match.'

    $valid = Invoke-Evidence -CaseName 'valid' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Missing evidence is rejected by scripts/Test-New-QaEvidence.ps1.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@
    Assert-Equal $valid.Failure $null 'Valid evidence should pass.'
    Assert-Equal $valid.Result.status 'passed' 'Valid evidence should be recorded as passed.'
    Assert-Equal $valid.Result.knowledgeRevision 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' 'The cited knowledge revision should be preserved.'
    Assert-Equal $valid.Result.trustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' 'The trusted revision should be preserved separately.'
    Assert-Equal (
        $valid.Result.metadataDigest -match '^[0-9a-f]{64}$'
    ) $true 'QA evidence must bind a SHA-256 digest of the reviewed title and body.'

    $placeholder = Invoke-Evidence -CaseName 'placeholder' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

## Acceptance criteria evidence

- [x] Criterion and evidence location

## Negative-path evidence

- Negative path and test or other evidence

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@
    Assert-Equal $placeholder.Result.status 'failed' 'Template placeholders must fail.'
    Assert-Equal ($null -eq $placeholder.Failure) $false 'Template placeholders must throw.'

    $ambiguousRevision = Invoke-Evidence -CaseName 'ambiguous-revision' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Missing evidence is rejected by scripts/Test-New-QaEvidence.ps1.

## Knowledge revision

Compared `bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb` with `cccccccccccccccccccccccccccccccccccccccc`.
'@
    Assert-Equal $ambiguousRevision.Result.status 'failed' 'Ambiguous knowledge revisions must fail.'
    Assert-Equal ($null -eq $ambiguousRevision.Failure) $false 'Ambiguous knowledge revisions must throw.'

    $nonProduct = Invoke-Evidence -CaseName 'non-product' -Title 'Harden platform paths' -Body @'
## Work tracking

- Azure Boards: N/A
- Platform change: true

# Pipeline change
'@
    Assert-Equal $nonProduct.Failure $null 'Non-product evidence should pass without product sections.'
    Assert-Equal $nonProduct.Result.knowledgeRevisionEvidence 'N/A - non-product change.' 'Non-product evidence should record an explicit N/A.'
    Assert-Equal $nonProduct.Result.trustedKnowledgeRevision 'dddddddddddddddddddddddddddddddddddddddd' 'Non-product evidence should retain the trusted revision.'
    Assert-Equal $nonProduct.Result.classification 'platform' 'Platform src changes should retain truthful platform classification.'

    $missingIdentity = Invoke-Evidence -CaseName 'missing-identity' -Title 'Change product behavior' -Body @'
## Work tracking

- Platform change: false

## Acceptance criteria evidence

- [x] Product behavior is covered.

## Negative-path evidence

- Missing identity is rejected.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@
    Assert-Equal $missingIdentity.Result.status 'failed' 'Product src changes without AB identity must fail.'
    Assert-Equal ($null -eq $missingIdentity.Failure) $false 'Missing product identity must throw.'

    $contradictoryPlatform = Invoke-Evidence -CaseName 'contradictory-platform' -Title 'Harden platform paths' -Body @'
## Work tracking

- Azure Boards: N/A
- Platform change: false
- Platform change: true
'@
    Assert-Equal $contradictoryPlatform.Result.status 'failed' 'Contradictory platform declarations must fail.'
    Assert-Equal ($null -eq $contradictoryPlatform.Failure) $false 'Contradictory platform declarations must throw.'

    $missingPlatform = Invoke-Evidence -CaseName 'missing-platform' -Body @'
## Work tracking

- Azure Boards: AB#999
'@
    Assert-Equal $missingPlatform.Result.status 'failed' 'Missing platform declarations must fail.'
    Assert-Equal ($null -eq $missingPlatform.Failure) $false 'Missing platform declarations must throw.'

    $invalidPlatform = Invoke-Evidence -CaseName 'invalid-platform' -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: yes
'@
    Assert-Equal $invalidPlatform.Result.status 'failed' 'Invalid platform declarations must fail.'
    Assert-Equal ($null -eq $invalidPlatform.Failure) $false 'Invalid platform declarations must throw.'

    $conflictingIdentity = Invoke-Evidence -CaseName 'conflicting-identity' -Body @'
## Work tracking

- Azure Boards: N/A
- Azure Boards: AB#999
- Platform change: true
'@
    Assert-Equal $conflictingIdentity.Result.status 'failed' 'Azure Boards N/A plus AB identity must fail.'
    Assert-Equal ($null -eq $conflictingIdentity.Failure) $false 'Conflicting Azure Boards declarations must throw.'

    $mixedIdentity = Invoke-Evidence -CaseName 'mixed-identity' -Title 'Harden platform paths' -Body @'
## Work tracking

- Azure Boards: N/A; AB#999
- Platform change: true
'@
    Assert-Equal $mixedIdentity.Result.status 'failed' 'Mixed N/A and AB identity must fail.'
    Assert-Equal ($null -eq $mixedIdentity.Failure) $false 'Mixed N/A and AB identity must throw.'

    $zeroTests = Invoke-Evidence `
        -CaseName 'zero-tests' `
        -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Zero test runs are rejected.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@ `
        -Counters 'total="0" executed="0" passed="0" failed="0"'
    Assert-Equal $zeroTests.Result.status 'failed' 'Zero executed tests must fail.'
    Assert-Equal ($null -eq $zeroTests.Failure) $false 'Zero executed tests must throw.'

    $failedTests = Invoke-Evidence `
        -CaseName 'failed-tests' `
        -Body @'
## Work tracking

- Azure Boards: AB#999
- Platform change: false

## Acceptance criteria evidence

- [x] QA policy records cited evidence.

## Negative-path evidence

- Failed tests are rejected.

## Knowledge revision

`bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb`
'@ `
        -Counters 'total="2" executed="2" passed="1" failed="1"'
    Assert-Equal $failedTests.Result.status 'failed' 'Any non-passing test must fail.'
    Assert-Equal ($null -eq $failedTests.Failure) $false 'Any non-passing test must throw.'
}
finally {
    Remove-Item $testRoot -Recurse -Force
}

Write-Output 'New-QaEvidence tests passed.'
exit 0
