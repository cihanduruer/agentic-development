[CmdletBinding()]
param(
    [string] $WorkflowPath =
        (Join-Path $PSScriptRoot '../.github/workflows/qa-evidence.yml')
)

$ErrorActionPreference = 'Stop'

function Assert-Equal {
    param(
        [Parameter(Mandatory)][AllowNull()] $Actual,
        [Parameter(Mandatory)][AllowNull()] $Expected,
        [Parameter(Mandatory)][string] $Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

$workflow = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $WorkflowPath))
$lines = $workflow -split '\r?\n'
$stepIndex = [Array]::IndexOf($lines, '      - name: Check current review requirement')
if ($stepIndex -lt 0) {
    throw 'The eligibility job must contain the Check current review requirement step.'
}
$scriptIndex = -1
for ($index = $stepIndex + 1; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -ceq '          script: |') {
        $scriptIndex = $index
        break
    }
    if ($lines[$index] -match '^      - ') {
        break
    }
}
if ($scriptIndex -lt 0) {
    throw 'The review-eligibility step must contain a literal script block.'
}
$scriptLines = [Collections.Generic.List[string]]::new()
for ($index = $scriptIndex + 1; $index -lt $lines.Count; $index++) {
    $line = $lines[$index]
    if ([string]::IsNullOrWhiteSpace($line)) {
        $scriptLines.Add('')
        continue
    }
    if (-not $line.StartsWith('            ', [StringComparison]::Ordinal)) {
        break
    }
    $scriptLines.Add($line.Substring(12))
}
while ($scriptLines.Count -gt 0 -and $scriptLines[$scriptLines.Count - 1] -eq '') {
    $scriptLines.RemoveAt($scriptLines.Count - 1)
}
$eligibilityScript = ($scriptLines -join "`n") + "`n"
$scriptContract = [IO.File]::ReadAllText(
    (Join-Path $PSScriptRoot 'assert_qa_test_selection.py'))
$pinnedScriptHash = [regex]::Match(
    $scriptContract,
    'QA_REVIEW_ELIGIBILITY_SCRIPT_SHA256 = \(\s*"(?<hash>[0-9a-f]{64})"').Groups['hash'].Value
$actualScriptHash = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($eligibilityScript))).ToLowerInvariant()
Assert-Equal $actualScriptHash $pinnedScriptHash 'The exercised review-eligibility script must be pinned.'

$uploadStep = [regex]::Match(
    $workflow,
    '(?ms)^      - name: Upload QA evidence\r?\n(?<step>.*?)(?=^      - name:|\z)')
if (-not $uploadStep.Success) {
    throw 'The QA workflow must contain the Upload QA evidence step.'
}
$uploadCondition = [regex]::Match(
    $uploadStep.Groups['step'].Value,
    '(?m)^        if:[ \t]*(?<condition>[^\r\n]+)')
if (-not $uploadCondition.Success -or
    $uploadCondition.Groups['condition'].Value -cne "always() && steps.qa-inputs.outcome == 'success'") {
    throw 'QA artifact upload must run only after its evidence inputs were generated successfully.'
}
if (-not [regex]::IsMatch(
        $workflow,
        '(?ms)^      - name: Write pull request evidence\r?\n(?:(?!^      - name:).)*?^        id: qa-inputs$')) {
    throw 'The QA evidence-input step must expose the upload gate outcome.'
}

function Should-UploadEvidence {
    param([Parameter(Mandatory)][string] $InputOutcome)
    $InputOutcome -ceq 'success'
}

Assert-Equal (Should-UploadEvidence 'skipped') $false 'Upload after context failure'
Assert-Equal (Should-UploadEvidence 'failure') $false 'Upload after evidence-input failure'
Assert-Equal (Should-UploadEvidence 'success') $true 'Upload after successful evidence inputs'

$harness = @'
const fs = require('fs');
const [scriptPath, scenarioPath] = process.argv.slice(2);
const script = fs.readFileSync(scriptPath, 'utf8');
const scenario = JSON.parse(fs.readFileSync(scenarioPath, 'utf8'));
const outputs = {};
const calls = { pulls: 0, associated: 0, jobs: 0 };
const github = {
  rest: {
    pulls: {
      get: async () => {
        calls.pulls++;
        return { data: scenario.pull };
      }
    },
    repos: {
      listPullRequestsAssociatedWithCommit: async () => {
        calls.associated++;
        return { data: scenario.associated || [] };
      }
    },
    actions: {
      listJobsForWorkflowRun: async () => {
        calls.jobs++;
        return { data: { jobs: scenario.jobs || [] } };
      }
    }
  }
};
const context = {
  eventName: scenario.eventName,
  repo: { owner: 'contoso', repo: 'hotel' },
  payload: scenario.payload
};
const core = {
  setOutput: (name, value) => { outputs[name] = String(value); },
  info: () => {}
};

(async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  await new AsyncFunction('github', 'context', 'core', script)(github, context, core);
  process.stdout.write(JSON.stringify({ outputs, calls }));
})().catch(error => {
  process.stderr.write(String(error));
  process.exitCode = 1;
});
'@

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "QaReviewEligibility-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $scriptPath = Join-Path $testRoot 'eligibility.js'
    $harnessPath = Join-Path $testRoot 'harness.js'
    [IO.File]::WriteAllText($scriptPath, $eligibilityScript)
    [IO.File]::WriteAllText($harnessPath, $harness)

    function Invoke-Scenario {
        param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][hashtable] $Scenario)
        $scenarioPath = Join-Path $testRoot "$Name.json"
        $Scenario | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $scenarioPath
        $output = & node $harnessPath $scriptPath $scenarioPath
        if ($LASTEXITCODE -ne 0) {
            throw "Eligibility harness failed for '$Name': $output"
        }
        ($output -join "`n") | ConvertFrom-Json
    }

    $headSha = '1111111111111111111111111111111111111111'
    $pullRequest = @{
        number = 42
        state = 'open'
        draft = $false
        base = @{ ref = 'main'; repo = @{ full_name = 'contoso/hotel' } }
        head = @{ sha = $headSha; repo = @{ full_name = 'contoso/hotel' } }
    }
    $reviewRun = @{
        id = 5001
        name = 'Hotel code review'
        event = 'pull_request_target'
        conclusion = 'success'
        head_sha = $headSha
        head_repository = @{ full_name = 'contoso/hotel' }
        pull_requests = @(@{ number = 42 })
    }
    $passedReview = @{ name = 'Copilot findings gate'; status = 'completed'; conclusion = 'success' }
    $draftPullRequest = $pullRequest.Clone()
    $draftPullRequest.draft = $true

    $result = Invoke-Scenario 'draft-review' @{
        eventName = 'workflow_run'
        payload = @{ workflow_run = $reviewRun }
        pull = $draftPullRequest
        jobs = @($passedReview)
    }
    Assert-Equal $result.outputs.run_qa 'false' 'Draft review'
    Assert-Equal $result.calls.jobs 0 'Draft review must not inspect or launch QA.'

    $result = Invoke-Scenario 'review-not-required' @{
        eventName = 'workflow_run'
        payload = @{ workflow_run = $reviewRun }
        pull = $pullRequest
        jobs = @(@{ name = 'Copilot review not required'; status = 'completed'; conclusion = 'success' })
    }
    Assert-Equal $result.outputs.run_qa 'false' 'Review not required'
    Assert-Equal $result.calls.jobs 1 'A non-draft review must be checked for the required review job.'

    $result = Invoke-Scenario 'current-head-review-passed' @{
        eventName = 'workflow_run'
        payload = @{ workflow_run = $reviewRun }
        pull = $pullRequest
        jobs = @($passedReview)
    }
    Assert-Equal $result.outputs.run_qa 'true' 'Current exact-head review'

    $stalePullRequest = $pullRequest.Clone()
    $stalePullRequest.head = @{
        sha = '3333333333333333333333333333333333333333'
        repo = @{ full_name = 'contoso/hotel' }
    }
    $result = Invoke-Scenario 'review-head-stale' @{
        eventName = 'workflow_run'
        payload = @{ workflow_run = $reviewRun }
        pull = $stalePullRequest
        jobs = @($passedReview)
    }
    Assert-Equal $result.outputs.run_qa 'false' 'Stale reviewed head'
    Assert-Equal $result.calls.jobs 0 'A stale review must not inspect jobs or launch QA.'

    $result = Invoke-Scenario 'manual-main-dispatch' @{
        eventName = 'workflow_dispatch'
        payload = @{ inputs = @{} }
    }
    Assert-Equal $result.outputs.run_qa 'true' 'Manual dispatch remains available for exact-SHA validation'

    $result = Invoke-Scenario 'trusted-main-validation' @{
        eventName = 'workflow_run'
        payload = @{
            workflow_run = @{
                name = 'PR validation'
                event = 'push'
                head_branch = 'main'
                conclusion = 'success'
                repository = @{ full_name = 'contoso/hotel' }
                head_repository = @{ full_name = 'contoso/hotel' }
            }
        }
    }
    Assert-Equal $result.outputs.run_qa 'true' 'Trusted successful main validation remains available'

    Write-Host 'QA review eligibility and artifact upload gate tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
