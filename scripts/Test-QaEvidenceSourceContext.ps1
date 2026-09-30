[CmdletBinding()]
param(
    [string] $WorkflowPath =
        (Join-Path $PSScriptRoot '../.github/workflows/qa-evidence.yml')
)

$ErrorActionPreference = 'Stop'

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

function Get-SourceContextScript {
    param([Parameter(Mandatory)][string] $Workflow)

    $lines = $Workflow -split '\r?\n'
    $stepIndex = [Array]::IndexOf($lines, '      - name: Resolve source context')
    if ($stepIndex -lt 0) {
        throw 'The QA workflow must contain the Resolve source context step.'
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
        throw 'The source-context step must contain a literal script block.'
    }
    $indent = ' ' * 12
    $body = [Collections.Generic.List[string]]::new()
    for ($index = $scriptIndex + 1; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ([string]::IsNullOrWhiteSpace($line)) {
            $body.Add('')
            continue
        }
        if (-not $line.StartsWith($indent, [StringComparison]::Ordinal)) {
            break
        }
        $body.Add($line.Substring($indent.Length))
    }
    while ($body.Count -gt 0 -and $body[$body.Count - 1] -eq '') {
        $body.RemoveAt($body.Count - 1)
    }
    ($body -join "`n") + "`n"
}

$harness = @'
const crypto = require('crypto');
const fs = require('fs');
const [scriptPath, scenarioPath] = process.argv.slice(2);
const script = fs.readFileSync(scriptPath, 'utf8');
const scenario = JSON.parse(fs.readFileSync(scenarioPath, 'utf8'));
const owner = 'contoso';
const repo = 'hotel';
const fullName = `${owner}/${repo}`;
const realSetImmediate = setImmediate;
let now = Date.parse('2026-09-30T14:41:00Z');
const start = now;
const sleeps = [];
Date.now = () => now;
globalThis.setTimeout = (callback, milliseconds) => {
  sleeps.push(milliseconds);
  now += milliseconds;
  realSetImmediate(callback);
  return 0;
};

const counts = {};
const log = [];
const runGets = {};
const record = (endpoint, params) => {
  counts[endpoint] = (counts[endpoint] || 0) + 1;
  log.push({ endpoint, params });
  const index = counts[endpoint];
  const fault = (scenario.faults || []).find(item =>
    item.endpoint === endpoint && (item.always || item.call === index));
  if (fault) {
    const headers = { ...(fault.headers || {}) };
    if (fault.resetInSeconds !== undefined) {
      headers['x-ratelimit-reset'] = String(Math.floor(now / 1000) + fault.resetInSeconds);
    }
    const error = new Error(fault.message);
    error.name = 'HttpError';
    error.status = fault.status;
    error.response = { status: fault.status, headers };
    throw error;
  }
  return index;
};

const runs = {};
for (const item of scenario.runs || []) {
  runs[item.id] = item;
}
const artifacts = [...(scenario.artifacts || [])];
for (let index = 0; index < (scenario.historicalRuns || 0); index++) {
  const id = 100000 + index;
  runs[id] = {
    id,
    path: '.github/workflows/pr-validation.yml',
    event: 'workflow_dispatch',
    head_branch: 'main',
    status: 'completed',
    conclusion: 'success',
    repository: fullName,
    head_repository: fullName
  };
  artifacts.push({
    id: 700000 + index,
    name: `pr-validation-evidence-${crypto.createHash('sha1').update(String(index)).digest('hex')}`,
    runId: id
  });
}
const listingCall = () => Math.max(counts.listArtifactsForRepo || 0, counts.listWorkflowRuns || 0);
const shapeArtifact = item => ({
  id: item.id,
  name: item.name,
  expired: Boolean(item.expired),
  expires_at: item.expires_at || '2026-10-30T00:00:00Z',
  workflow_run: { id: item.runId, repository_id: 1, head_repository_id: 1 }
});
const visible = item => listingCall() >= (item.visibleFromListCall || 1);
const shapeRun = item => {
  const gets = runGets[item.id] || 0;
  const pending = (item.inProgressGets || 0) > 0 && gets <= item.inProgressGets;
  return {
    id: item.id,
    path: item.path,
    event: item.event,
    head_branch: item.head_branch,
    status: pending ? 'in_progress' : item.status,
    conclusion: pending ? null : item.conclusion,
    repository: { full_name: item.repository },
    head_repository: { full_name: item.head_repository }
  };
};
const pullHeads = scenario.pullHeads || [scenario.sourceSha];
const makePull = headSha => ({
  number: 42,
  state: scenario.pullState || 'open',
  title: 'Harden platform',
  body: 'Platform change: true',
  merged_at: scenario.mergedAt || null,
  base: { ref: 'main', sha: scenario.baseSha, repo: { full_name: fullName } },
  head: { sha: headSha, repo: { full_name: fullName } }
});

const github = {
  rest: {
    pulls: {
      get: async params => {
        const index = record('pulls.get', params);
        return { data: makePull(pullHeads[Math.min(index, pullHeads.length) - 1]) };
      },
      listFiles: async params => {
        record('pulls.listFiles', params);
        return { data: (scenario.files || []).map(filename => ({ filename })) };
      }
    },
    repos: {
      listPullRequestsAssociatedWithCommit: async params => {
        record('repos.listPullRequestsAssociatedWithCommit', params);
        return { data: [makePull(pullHeads[0])] };
      }
    },
    actions: {
      listArtifactsForRepo: async params => {
        record('listArtifactsForRepo', params);
        const matches = artifacts.filter(item =>
          (params.name === undefined || item.name === params.name || item.forceListed)
          && visible(item));
        return { data: { total_count: matches.length, artifacts: matches.slice(0, 100).map(shapeArtifact) } };
      },
      getWorkflowRun: async params => {
        record('getWorkflowRun', params);
        runGets[params.run_id] = (runGets[params.run_id] || 0) + 1;
        const item = runs[params.run_id];
        if (!item) {
          const error = new Error('Not Found');
          error.status = 404;
          error.response = { status: 404, headers: {} };
          throw error;
        }
        return { data: shapeRun(item) };
      },
      listWorkflowRuns: async params => {
        record('listWorkflowRuns', params);
        const all = Object.values(runs).sort((left, right) => right.id - left.id).map(shapeRun);
        return { data: { total_count: all.length, workflow_runs: all.slice(0, params.per_page || 30) } };
      },
      listWorkflowRunArtifacts: async params => {
        record('listWorkflowRunArtifacts', params);
        const matches = artifacts.filter(item =>
          item.runId === params.run_id
          && !item.detached
          && (params.name === undefined || item.name === params.name)
          && visible(item));
        return { data: { total_count: matches.length, artifacts: matches.map(shapeArtifact) } };
      }
    }
  },
  paginate: async (method, params) => {
    const response = await method(params);
    return Array.isArray(response.data)
      ? response.data
      : response.data.artifacts || response.data.workflow_runs;
  }
};

const outputs = {};
const failures = [];
const warnings = [];
const core = {
  setFailed: message => failures.push(String(message)),
  setOutput: (name, value) => { outputs[name] = String(value); },
  info: () => {},
  warning: message => warnings.push(String(message))
};
const context = {
  eventName: scenario.eventName,
  ref: scenario.ref,
  repo: { owner, repo },
  payload: scenario.payload
};

(async () => {
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  try {
    await new AsyncFunction('require', 'github', 'context', 'core', script)(require, github, context, core);
  } catch (error) {
    failures.push(`Unhandled error: ${error}`);
  }
  const total = Object.values(counts).reduce((sum, value) => sum + value, 0);
  process.stdout.write(JSON.stringify({
    failures,
    outputs,
    warnings,
    counts,
    total,
    sleeps,
    elapsedMilliseconds: now - start,
    runIds: log.filter(item => item.endpoint === 'getWorkflowRun').map(item => item.params.run_id),
    runArtifactRunIds: log.filter(item => item.endpoint === 'listWorkflowRunArtifacts').map(item => item.params.run_id),
    artifactNames: log.filter(item => item.endpoint === 'listArtifactsForRepo').map(item => item.params.name)
  }));
})();
'@

$sourceSha = '1111111111111111111111111111111111111111'
$baseSha = '2222222222222222222222222222222222222222'
$repository = 'contoso/hotel'
$artifactName = "pr-validation-evidence-$sourceSha"
$trustedRun = @{
    id = 5001; path = '.github/workflows/pr-validation.yml'; event = 'workflow_dispatch'
    head_branch = 'main'; status = 'completed'; conclusion = 'success'
    repository = $repository; head_repository = $repository
}

function New-Run {
    param([hashtable] $Overrides)
    $run = $trustedRun.Clone()
    foreach ($key in $Overrides.Keys) {
        $run[$key] = $Overrides[$key]
    }
    $run
}

$decoyRuns = @(
    (New-Run @{ id = 5002; event = 'pull_request'; head_branch = 'feature' }),
    (New-Run @{ id = 5003; event = 'push' }),
    (New-Run @{ id = 5004; head_branch = 'feature' }),
    (New-Run @{ id = 5005; path = '.github/workflows/other.yml' }),
    (New-Run @{ id = 5010; head_repository = 'fork/hotel' }),
    (New-Run @{ id = 5006; conclusion = 'failure' }),
    (New-Run @{ id = 5007; status = 'in_progress'; conclusion = $null; inProgressGets = 1000 }),
    (New-Run @{ id = 5008 }),
    (New-Run @{ id = 5009 })
)
$decoyArtifacts = @(
    @{ id = 6002; name = $artifactName; runId = 5002 },
    @{ id = 6003; name = $artifactName; runId = 5003 },
    @{ id = 6004; name = $artifactName; runId = 5004 },
    @{ id = 6005; name = $artifactName; runId = 5005 },
    @{ id = 6010; name = $artifactName; runId = 5010 },
    @{ id = 6006; name = $artifactName; runId = 5006 },
    @{ id = 6007; name = $artifactName; runId = 5007 },
    @{ id = 6008; name = $artifactName; runId = 5008; expired = $true },
    @{ id = 6009; name = "$artifactName-copy"; runId = 5009; forceListed = $true }
)
$trustedArtifact = @{ id = 6001; name = $artifactName; runId = 5001 }
$immutableDecoyRunIds = @(5002, 5003, 5004, 5005, 5010)

function New-ManualScenario {
    param([hashtable] $Overrides = @{})
    $scenario = @{
        eventName = 'workflow_dispatch'
        ref = 'refs/heads/main'
        payload = @{ inputs = @{ pull_request_number = '42'; head_sha = $sourceSha } }
        sourceSha = $sourceSha
        baseSha = $baseSha
        files = @('.github/workflows/qa-evidence.yml', 'docs/knowledge/agentic-delivery.md')
        historicalRuns = 150
        runs = @($trustedRun)
        artifacts = @($trustedArtifact)
        faults = @()
    }
    foreach ($key in $Overrides.Keys) {
        $scenario[$key] = $Overrides[$key]
    }
    $scenario
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][hashtable] $Scenario
    )
    $scenarioPath = Join-Path $testRoot "$Name.json"
    $Scenario | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $scenarioPath
    $output = & node $harnessPath $scriptPath $scenarioPath
    if ($LASTEXITCODE -ne 0) {
        throw "Harness failed for '$Name': $output"
    }
    $result = ($output -join "`n") | ConvertFrom-Json
    Write-Host ("Scenario '{0}': {1} GitHub API requests ({2}); {3} waits; failure: {4}" -f
        $Name,
        $result.total,
        (($result.counts.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ', '),
        @($result.sleeps).Count,
        (@($result.failures) -join ' | '))
    $result
}

function Get-Count {
    param($Result, [string] $Endpoint)
    $property = $Result.counts.PSObject.Properties[$Endpoint]
    if ($null -eq $property) { 0 } else { [int] $property.Value }
}

function Assert-Failure {
    param($Result, [string] $Contains, [string] $Message)
    Assert-Equal @($Result.failures).Count 1 "$Message must fail exactly once."
    Assert-Equal ([string] $Result.failures[0]).Contains($Contains) $true "$Message failure '$($Result.failures[0])' must mention '$Contains'."
}

function Assert-Success {
    param($Result, [string] $Message)
    Assert-Equal @($Result.failures).Count 0 "$Message must succeed: $(@($Result.failures) -join ' | ')."
    Assert-Equal $Result.outputs.number '42' "$Message PR number output."
    Assert-Equal $Result.outputs.'head-sha' $sourceSha "$Message head SHA output."
    Assert-Equal $Result.outputs.'source-repository' $repository "$Message source repository output."
    Assert-Equal $Result.outputs.'trusted-knowledge-revision' $baseSha "$Message trusted knowledge revision output."
    Assert-Equal $Result.outputs.title 'Harden platform' "$Message title output."
    Assert-Equal `
        ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Result.outputs.'body-base64'))) `
        'Platform change: true' `
        "$Message body output."
    Assert-Equal `
        ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Result.outputs.'files-base64'))) `
        ".github/workflows/qa-evidence.yml`ndocs/knowledge/agentic-delivery.md" `
        "$Message files output."
}

function Assert-NoHistoricalScan {
    param($Result, [string] $Message)
    Assert-Equal (Get-Count $Result 'listWorkflowRuns') 0 "$Message must not list historical workflow runs."
    Assert-Equal (@($Result.runIds | Where-Object { $_ -ge 100000 }).Count) 0 "$Message must not inspect unrelated historical runs."
    Assert-Equal (@($Result.runArtifactRunIds | Where-Object { $_ -ne 5001 }).Count) 0 "$Message may list run artifacts only for a verified trusted run."
    Assert-Equal (@($Result.artifactNames | Where-Object { $_ -cne $artifactName }).Count) 0 "$Message must filter the repository artifact listing by the exact name."
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) "QaEvidenceSourceContext-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $workflow = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $WorkflowPath))
    $script = Get-SourceContextScript -Workflow $workflow
    $pinnedHash = [regex]::Match(
        [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'assert_qa_test_selection.py')),
        '"(?<hash>[0-9a-f]{64})"').Groups['hash'].Value
    $scriptHash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($script))).ToLowerInvariant()
    Assert-Equal $scriptHash $pinnedHash 'The exercised source-context script must be the pinned workflow script.'

    $scriptPath = Join-Path $testRoot 'source-context.js'
    $harnessPath = Join-Path $testRoot 'harness.js'
    [IO.File]::WriteAllText($scriptPath, $script)
    [IO.File]::WriteAllText($harnessPath, $harness)

    $result = Invoke-Scenario 'trusted-among-history-and-decoys' (New-ManualScenario @{
        runs = @($decoyRuns + $trustedRun)
        artifacts = @($decoyArtifacts + $trustedArtifact)
    })
    Assert-Success $result 'Trusted evidence among decoys'
    Assert-NoHistoricalScan $result 'Trusted evidence among decoys'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 1 'One exact-name listing must suffice.'
    Assert-Equal (Get-Count $result 'getWorkflowRun') 8 'Only unexpired exact-name artifact runs may be inspected.'
    Assert-Equal (@($result.runIds | Where-Object { $_ -in @(5008, 5009) }).Count) 0 'Expired and wrong-name artifacts must not be trusted or inspected.'
    Assert-Equal (Get-Count $result 'listWorkflowRunArtifacts') 1 'Only the verified run attachment may be checked.'
    Assert-Equal $result.total 12 'Total requests must stay bounded.'
    Assert-Equal @($result.sleeps).Count 0 'Available evidence must not wait.'

    $result = Invoke-Scenario 'delayed-artifact-repeated-waits' (New-ManualScenario @{
        runs = @($decoyRuns + $trustedRun)
        artifacts = @($decoyArtifacts + @{ id = 6001; name = $artifactName; runId = 5001; visibleFromListCall = 5 })
    })
    Assert-Success $result 'Delayed artifact'
    Assert-NoHistoricalScan $result 'Delayed artifact'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 5 'Each wait must issue one exact-name listing.'
    foreach ($runId in $immutableDecoyRunIds) {
        Assert-Equal (@($result.runIds | Where-Object { $_ -eq $runId }).Count) 1 "Immutable rejection of run $runId must be cached."
    }
    Assert-Equal (@($result.runIds | Where-Object { $_ -eq 5006 }).Count) 5 'A failed run may be re-run and must be rechecked.'
    Assert-Equal (@($result.runIds | Where-Object { $_ -eq 5007 }).Count) 5 'An in-progress run must be rechecked.'
    Assert-Equal (Get-Count $result 'getWorkflowRun') 16 'Repeated waits must not rescan immutable decisions.'
    Assert-Equal $result.total 24 'Delayed artifact requests must stay bounded.'
    Assert-Equal @($result.sleeps).Count 4 'Delayed artifact must wait four times.'

    $result = Invoke-Scenario 'in-progress-then-success' (New-ManualScenario @{
        runs = @((New-Run @{ inProgressGets = 2 }))
    })
    Assert-Success $result 'In-progress validation'
    Assert-Equal (Get-Count $result 'getWorkflowRun') 3 'An in-progress trusted run must be rechecked until complete.'
    Assert-Equal (Get-Count $result 'listWorkflowRunArtifacts') 1 'Attachment must be checked only after success.'
    Assert-Equal @($result.sleeps).Count 2 'In-progress validation must wait twice.'

    $result = Invoke-Scenario 'no-artifact' (New-ManualScenario @{ runs = @(); artifacts = @() })
    Assert-Failure $result 'No successful trusted PR validation evidence exists for this commit.' 'Missing evidence'
    Assert-NoHistoricalScan $result 'Missing evidence'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 60 'Missing evidence must poll exactly 60 times.'
    Assert-Equal $result.total 61 'Missing evidence must cost one request per attempt plus the PR read.'
    Assert-Equal @($result.sleeps).Count 59 'Missing evidence must not wait after the final attempt.'

    $result = Invoke-Scenario 'only-untrusted-evidence' (New-ManualScenario @{
        runs = $decoyRuns
        artifacts = $decoyArtifacts
    })
    Assert-Failure $result 'No successful trusted PR validation evidence exists for this commit.' 'Untrusted evidence'
    Assert-NoHistoricalScan $result 'Untrusted evidence'
    Assert-Equal (Get-Count $result 'listWorkflowRunArtifacts') 0 'Untrusted runs must never be accepted.'
    Assert-Equal (Get-Count $result 'getWorkflowRun') 125 'Only mutable run decisions may be rechecked.'

    $result = Invoke-Scenario 'artifact-not-attached-to-run' (New-ManualScenario @{
        historicalRuns = 0
        artifacts = @(@{ id = 6001; name = $artifactName; runId = 5001; detached = $true })
    })
    Assert-Failure $result 'No successful trusted PR validation evidence exists for this commit.' 'Detached artifact'
    Assert-Equal (Get-Count $result 'listWorkflowRunArtifacts') 60 'Detached artifact must be rechecked, never accepted.'

    $result = Invoke-Scenario 'primary-rate-limit-reset' (New-ManualScenario @{
        faults = @(@{ endpoint = 'listArtifactsForRepo'; call = 1; status = 403; message = 'API rate limit exceeded for installation.'; headers = @{ 'x-ratelimit-remaining' = '0' }; resetInSeconds = 30 })
    })
    Assert-Success $result 'Primary rate limit'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 2 'Rate-limited listing must retry once after reset.'
    Assert-Equal ($result.sleeps -join ',') '31000' 'Primary rate limit must wait until reset.'
    Assert-Equal @($result.warnings).Count 1 'Rate-limit wait must be visible.'

    $result = Invoke-Scenario 'retry-after-429' (New-ManualScenario @{
        faults = @(@{ endpoint = 'getWorkflowRun'; call = 1; status = 429; message = 'Too Many Requests'; headers = @{ 'retry-after' = '5' } })
    })
    Assert-Success $result 'Retry-After 429'
    Assert-Equal ($result.sleeps -join ',') '5000' 'HTTP 429 must honor Retry-After.'

    $result = Invoke-Scenario 'secondary-rate-limit' (New-ManualScenario @{
        faults = @(@{ endpoint = 'listArtifactsForRepo'; call = 1; status = 403; message = 'You have exceeded a secondary rate limit.' })
    })
    Assert-Success $result 'Secondary rate limit'
    Assert-Equal ($result.sleeps -join ',') '60000' 'Secondary rate limit without headers must wait one minute.'

    $result = Invoke-Scenario 'rate-limit-beyond-deadline' (New-ManualScenario @{
        faults = @(@{ endpoint = 'listArtifactsForRepo'; always = $true; status = 403; message = 'API rate limit exceeded for installation.'; headers = @{ 'x-ratelimit-remaining' = '0' }; resetInSeconds = 1790 })
    })
    Assert-Failure $result 'Rerun QA evidence after' 'Rate limit beyond deadline'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 1 'A distant reset must fail without further requests.'
    Assert-Equal @($result.sleeps).Count 0 'A distant reset must not wait.'

    $result = Invoke-Scenario 'persistent-rate-limit' (New-ManualScenario @{
        faults = @(@{ endpoint = 'listArtifactsForRepo'; always = $true; status = 429; message = 'Too Many Requests'; headers = @{ 'retry-after' = '1' } })
    })
    Assert-Failure $result 'Rerun QA evidence after' 'Persistent rate limit'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 6 'Rate-limit retries must be bounded.'

    $limited = @{ status = 429; message = 'Too Many Requests'; headers = @{ 'retry-after' = '1' } }
    $result = Invoke-Scenario 'job-wide-rate-limit-budget' (New-ManualScenario @{
        faults = @(
            foreach ($call in 1..3) { $limited + @{ endpoint = 'listArtifactsForRepo'; call = $call } }
            foreach ($call in 1..3) { $limited + @{ endpoint = 'getWorkflowRun'; call = $call } }
        )
    })
    Assert-Failure $result 'Rerun QA evidence after' 'Job-wide rate-limit budget'
    Assert-Equal @($result.sleeps).Count 5 'Rate-limit waits must be capped across all requests in the job.'
    Assert-Equal (Get-Count $result 'getWorkflowRun') 3 'The sixth rate-limited response must fail without another request.'

    $result = Invoke-Scenario 'rate-limit-consumes-deadline' (New-ManualScenario @{
        artifacts = @()
        faults = @(@{ endpoint = 'listArtifactsForRepo'; call = 1; status = 429; message = 'Too Many Requests'; headers = @{ 'retry-after' = '800' } })
    })
    Assert-Failure $result 'No successful trusted PR validation evidence exists for this commit.' 'Deadline'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 12 'Polling must stop at the overall deadline.'
    Assert-Equal ($result.elapsedMilliseconds -le 900000) $true 'Waiting must not exceed the overall deadline.'

    $result = Invoke-Scenario 'authorization-403' (New-ManualScenario @{
        faults = @(@{ endpoint = 'listArtifactsForRepo'; call = 1; status = 403; message = 'Resource not accessible by integration' })
    })
    Assert-Failure $result 'Resource not accessible by integration' 'Authorization failure'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 1 'Authorization failures must not be retried.'
    Assert-Equal @($result.sleeps).Count 0 'Authorization failures must not wait.'

    $result = Invoke-Scenario 'server-error' (New-ManualScenario @{
        faults = @(@{ endpoint = 'getWorkflowRun'; call = 1; status = 500; message = 'Server Error' })
    })
    Assert-Failure $result 'Server Error' 'Server error'
    Assert-Equal (Get-Count $result 'getWorkflowRun') 1 'Server errors must fail explicitly.'

    $result = Invoke-Scenario 'manual-wrong-ref' (New-ManualScenario @{ ref = 'refs/heads/feature' })
    Assert-Failure $result 'Manual QA fallback must run from main.' 'Manual non-main ref'
    Assert-Equal $result.total 0 'Manual non-main ref must fail before API requests.'

    $result = Invoke-Scenario 'manual-head-changed' (New-ManualScenario @{ pullHeads = @('3333333333333333333333333333333333333333') })
    Assert-Failure $result 'Manual QA target must be the current head' 'Manual stale head'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 0 'Manual stale head must fail before provenance discovery.'

    $reviewPayload = @{
        workflow_run = @{
            name = 'Hotel code review'
            head_sha = $sourceSha
            head_repository = @{ full_name = $repository }
            pull_requests = @(@{ number = 42 })
        }
    }
    $result = Invoke-Scenario 'review-run' (New-ManualScenario @{ eventName = 'workflow_run'; ref = 'refs/heads/main'; payload = $reviewPayload })
    Assert-Success $result 'Review-triggered QA'
    Assert-NoHistoricalScan $result 'Review-triggered QA'
    Assert-Equal (Get-Count $result 'pulls.get') 2 'Review-triggered QA must re-read the current PR head.'

    $result = Invoke-Scenario 'review-head-changed' (New-ManualScenario @{
        eventName = 'workflow_run'; payload = $reviewPayload
        pullHeads = @($sourceSha, '3333333333333333333333333333333333333333')
    })
    Assert-Failure $result 'The pull request head changed after the reviewed workflow run.' 'Review stale head'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 0 'Review stale head must fail before provenance discovery.'

    $result = Invoke-Scenario 'main-validation-run' (New-ManualScenario @{
        eventName = 'workflow_run'
        mergedAt = '2026-09-30T14:00:00Z'
        payload = @{
            workflow_run = @{
                name = 'PR validation'
                head_sha = $sourceSha
                head_repository = @{ full_name = $repository }
                pull_requests = @()
            }
        }
    })
    Assert-Success $result 'Main validation-triggered QA'
    Assert-Equal (Get-Count $result 'listArtifactsForRepo') 0 'Main validation-triggered QA needs no provenance discovery.'
    Assert-Equal $result.total 2 'Main validation-triggered QA request count.'

    Write-Host 'QA evidence source-context provenance discovery tests passed.'
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
