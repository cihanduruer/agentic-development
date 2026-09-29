param(
    [Parameter(Mandatory)]
    [string] $Root,

    [Parameter(Mandatory)]
    [string] $Endpoint,

    [Parameter(Mandatory)]
    [string] $Revision,

    [string] $Index = 'knowledge',

    [ValidateRange(1, 3600)]
    [int] $MaximumWaitSeconds = 600,

    [ValidateRange(1, 300)]
    [int] $RetryDelaySeconds = 15,

    [scriptblock] $InvokeIndexer,

    [scriptblock] $Sleep,

    [scriptblock] $GetElapsedSeconds
)

$ErrorActionPreference = 'Stop'
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

if ($null -eq $InvokeIndexer) {
    $InvokeIndexer = {
        param(
            [string[]] $IndexerArguments,
            [int] $AttemptTimeoutSeconds
        )

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = 'dotnet'
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in $IndexerArguments) {
            $startInfo.ArgumentList.Add($argument)
        }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        try {
            if (-not $process.Start()) {
                throw 'Unable to start the knowledge indexer process.'
            }
            $standardOutput = $process.StandardOutput.ReadToEndAsync()
            $standardError = $process.StandardError.ReadToEndAsync()
            $completed = $process.WaitForExit($AttemptTimeoutSeconds * 1000)
            if (-not $completed) {
                $process.Kill($true)
                $process.WaitForExit()
            }
            $output = @(
                $standardOutput.GetAwaiter().GetResult()
                $standardError.GetAwaiter().GetResult()
            ) -join [Environment]::NewLine
            [pscustomobject]@{
                ExitCode = if ($completed) { $process.ExitCode } else { -1 }
                Output = $output.Trim()
                TimedOut = -not $completed
            }
        }
        finally {
            $process.Dispose()
        }
    }
}
if ($null -eq $Sleep) {
    $Sleep = { param([int] $Seconds) Start-Sleep -Seconds $Seconds }
}
if ($null -eq $GetElapsedSeconds) {
    $GetElapsedSeconds = { [int][Math]::Floor($stopwatch.Elapsed.TotalSeconds) }
}

$arguments = @(
    'run',
    '--project', 'tools/KnowledgeIndexer/KnowledgeIndexer.csproj',
    '--no-restore',
    '--',
    '--root', $Root,
    '--endpoint', $Endpoint,
    '--revision', $Revision,
    '--index', $Index
)

$lastOutput = ''
while ($true) {
    $elapsedBeforeAttempt = & $GetElapsedSeconds
    $remainingSeconds = $MaximumWaitSeconds - $elapsedBeforeAttempt
    if ($remainingSeconds -le 0) {
        throw "Knowledge indexing exhausted the ${MaximumWaitSeconds}s Search RBAC propagation deadline. Final indexing error: $lastOutput"
    }

    $result = & $InvokeIndexer $arguments $remainingSeconds
    $lastOutput = $result.Output
    $elapsed = & $GetElapsedSeconds
    if ($result.TimedOut) {
        throw "Knowledge indexing exceeded its ${remainingSeconds}s child-process deadline within the ${MaximumWaitSeconds}s Search RBAC propagation budget. Final indexing error: $($result.Output)"
    }
    if ($elapsed -gt $MaximumWaitSeconds) {
        throw "Knowledge indexing exceeded the ${MaximumWaitSeconds}s Search RBAC propagation deadline. Final indexing error: $($result.Output)"
    }
    if ($result.ExitCode -eq 0) {
        Write-Output $result.Output
        break
    }

    if ($elapsed -ge $MaximumWaitSeconds) {
        throw "Knowledge indexing failed after waiting ${elapsed}s for Search RBAC propagation. Final indexing error: $($result.Output)"
    }

    $sleepSeconds = [Math]::Min($RetryDelaySeconds, $MaximumWaitSeconds - $elapsed)
    Write-Output "Knowledge indexing is not authorized or available after ${elapsed}s; retrying in ${sleepSeconds}s."
    & $Sleep $sleepSeconds
}
