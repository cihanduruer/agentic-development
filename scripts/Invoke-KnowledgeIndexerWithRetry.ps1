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
        param([string[]] $IndexerArguments)
        $output = & dotnet @IndexerArguments 2>&1
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = ($output -join [Environment]::NewLine)
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

while ($true) {
    $result = & $InvokeIndexer $arguments
    if ($result.ExitCode -eq 0) {
        Write-Output $result.Output
        break
    }

    $elapsed = & $GetElapsedSeconds
    if ($elapsed -ge $MaximumWaitSeconds) {
        throw "Knowledge indexing failed after waiting ${elapsed}s for Search RBAC propagation. Final indexing error: $($result.Output)"
    }

    Write-Output "Knowledge indexing is not authorized or available after ${elapsed}s; retrying in ${RetryDelaySeconds}s."
    & $Sleep $RetryDelaySeconds
}
