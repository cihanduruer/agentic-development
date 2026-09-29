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

foreach ($terminalState in @('Failed', 'Canceled')) {
    Invoke-Scenario -Responses @(
        @{ ExitCode = 0; Body = "{`"provisioningState`":`"$terminalState`"}" }
    ) -Assertion {
        & $scriptPath `
            -ResourceGroup test-rg `
            -DeploymentName test-deployment `
            -TimeoutSeconds 0 `
            -PollIntervalSeconds 0 `
            -AllowFailedTerminalState
    }
}

foreach ($terminalState in @('Succeeded', 'Failed', 'Canceled')) {
    Invoke-Scenario -Responses @(
        @{ ExitCode = 0; Body = '{"provisioningState":"Running"}' },
        @{ ExitCode = 0; Body = "{`"provisioningState`":`"$terminalState`"}" }
    ) -Assertion {
        & $scriptPath `
            -ResourceGroup test-rg `
            -DeploymentName test-deployment `
            -TimeoutSeconds 5 `
            -PollIntervalSeconds 0 `
            -AllowFailedTerminalState `
            -AllowNotFound
    }
}

Invoke-Scenario -Responses @(
    @{
        ExitCode = 1
        Body = "ERROR: (DeploymentNotFound) Deployment 'test-deployment' could not be found."
    },
    @{
        ExitCode = 1
        Body = "ERROR: (DeploymentNotFound) Deployment 'test-deployment' could not be found."
    },
    @{
        ExitCode = 1
        Body = "ERROR: (DeploymentNotFound) Deployment 'test-deployment' could not be found."
    }
) -Assertion {
    & $scriptPath `
        -ResourceGroup test-rg `
        -DeploymentName test-deployment `
        -TimeoutSeconds 5 `
        -PollIntervalSeconds 0 `
        -AllowFailedTerminalState `
        -AllowNotFound
}

Invoke-Scenario -Responses @(
    @{
        ExitCode = 1
        Body = "ERROR: (DeploymentNotFound) Deployment 'test-deployment' could not be found."
    },
    @{ ExitCode = 0; Body = '{"provisioningState":"Running"}' },
    @{ ExitCode = 0; Body = '{"provisioningState":"Succeeded"}' }
) -Assertion {
    & $scriptPath `
        -ResourceGroup test-rg `
        -DeploymentName test-deployment `
        -TimeoutSeconds 5 `
        -PollIntervalSeconds 0 `
        -AllowFailedTerminalState `
        -AllowNotFound
}

Invoke-Scenario -Responses @(
    @{
        ExitCode = 1
        Body = "ERROR: (DeploymentNotFound) Deployment 'test-deployment' could not be found."
    }
) -Assertion {
    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -DeploymentName test-deployment `
            -TimeoutSeconds 0 `
            -PollIntervalSeconds 0 `
            -AllowFailedTerminalState `
            -AllowNotFound
        throw 'A single not-found observation unexpectedly proved absence.'
    } catch {
        if ($_.Exception.Message -notmatch 'absence could not be proven') {
            throw
        }
    }
}

$misleadingNotFoundDiagnostics = @(
    "(AuthenticationFailed) Selected identity could not be found.",
    "(ResourceNotFound) Resource 'test-deployment' could not be found.",
    "(ServiceUnavailable) DeploymentNotFound status could not be confirmed.",
    "(DeploymentNotFound) Deployment 'other-deployment' could not be found.",
    "DeploymentNotFound",
    "ERROR: (DeploymentNotFound) Deployment 'test-deployment' could not be found.`nAdditional text"
)
foreach ($diagnostic in $misleadingNotFoundDiagnostics) {
    Invoke-Scenario -Responses @(
        @{ ExitCode = 1; Body = $diagnostic }
    ) -Assertion {
        try {
            & $scriptPath `
                -ResourceGroup test-rg `
                -DeploymentName test-deployment `
                -TimeoutSeconds 0 `
                -PollIntervalSeconds 0 `
                -AllowFailedTerminalState `
                -AllowNotFound
            throw 'A misleading not-found diagnostic was accepted.'
        } catch {
            if ($_.Exception.Message -notmatch 'Unable to read deployment') {
                throw
            }
        }
    }
}

Invoke-Scenario -Responses @(
    @{ ExitCode = 0; Body = '{"provisioningState":"Running"}' }
) -Assertion {
    try {
        & $scriptPath `
            -ResourceGroup test-rg `
            -DeploymentName test-deployment `
            -TimeoutSeconds 0 `
            -PollIntervalSeconds 0 `
            -AllowFailedTerminalState `
            -AllowNotFound
        throw 'A running deployment unexpectedly reached a terminal state.'
    } catch {
        if ($_.Exception.Message -notmatch 'did not finish') {
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
