$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Wait-AzureResourceGroupDeployment.ps1'
$outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "azure-deployment-$([Guid]::NewGuid()).txt"
$env:GITHUB_OUTPUT = $outputPath

function Invoke-Scenario {
    param(
        [Parameter(Mandatory)]
        [object[]] $Responses,

        [Parameter(Mandatory)]
        [scriptblock] $Assertion
    )

    $script:responseIndex = 0
    function global:az {
        $response = $Responses[[Math]::Min($script:responseIndex, $Responses.Count - 1)]
        $script:responseIndex++
        $global:LASTEXITCODE = $response.ExitCode
        if ($null -ne $response.Body) {
            $response.Body
        }
    }

    try {
        & $Assertion
    } finally {
        Remove-Item Function:\global:az -ErrorAction SilentlyContinue
        Remove-Item $outputPath -Force -ErrorAction SilentlyContinue
    }
}

Invoke-Scenario -Responses @(
    @{ ExitCode = 0; Body = '{"provisioningState":"Succeeded"}' }
) -Assertion {
    & $scriptPath -ResourceGroup test-rg -DeploymentName test-deployment -TimeoutSeconds 0 -PollIntervalSeconds 0
    if ((Get-Content $outputPath -Raw) -notmatch 'complete=true') {
        throw 'Successful deployment did not emit complete=true.'
    }
}

Invoke-Scenario -Responses @(
    @{ ExitCode = 0; Body = '{"provisioningState":"Running"}' }
) -Assertion {
    & $scriptPath -ResourceGroup test-rg -DeploymentName test-deployment -TimeoutSeconds 0 -PollIntervalSeconds 0 -AllowTimeout
    if ((Get-Content $outputPath -Raw) -notmatch 'complete=false') {
        throw 'Timed-out deployment did not emit complete=false.'
    }
}

Invoke-Scenario -Responses @(
    @{ ExitCode = 0; Body = '{"provisioningState":"Failed","error":{"code":"DeploymentFailed"}}' }
) -Assertion {
    try {
        & $scriptPath -ResourceGroup test-rg -DeploymentName test-deployment -TimeoutSeconds 0 -PollIntervalSeconds 0
        throw 'Failed deployment unexpectedly succeeded.'
    } catch {
        if ($_.Exception.Message -notmatch "ended in state 'Failed'.*DeploymentFailed") {
            throw
        }
    }
}

Invoke-Scenario -Responses @(
    @{ ExitCode = 1; Body = $null }
) -Assertion {
    try {
        & $scriptPath -ResourceGroup test-rg -DeploymentName test-deployment -TimeoutSeconds 0 -PollIntervalSeconds 0
        throw 'Unreadable deployment unexpectedly succeeded.'
    } catch {
        if ($_.Exception.Message -notmatch 'Unable to read deployment') {
            throw
        }
    }
}

Write-Output 'Azure resource-group deployment wait tests passed.'
exit 0
