[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ResourceGroup,

    [Parameter(Mandatory)]
    [string] $DeploymentName,

    [ValidateRange(0, 3600)]
    [int] $TimeoutSeconds = 2400,

    [ValidateRange(0, 300)]
    [int] $PollIntervalSeconds = 15,

    [switch] $AllowTimeout,

    [switch] $AllowFailedTerminalState,

    [switch] $AllowNotFound
)

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$notFoundObservations = 0
$requiredNotFoundObservations = 3
$deploymentObserved = $false

while ($true) {
    $deploymentJson = az deployment group show `
        --resource-group $ResourceGroup `
        --name $DeploymentName `
        --query properties `
        --output json `
        --only-show-errors 2>&1

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($deploymentJson)) {
        $diagnostic = "$deploymentJson".Trim()
        $escapedDeploymentName = [Regex]::Escape($DeploymentName)
        $deploymentNotFoundPattern =
            "^(?:ERROR:\s*)?\(DeploymentNotFound\)\s+Deployment\s+'$escapedDeploymentName'\s+could not be found\.?$"
        $jsonDiagnostic = $diagnostic -replace '^ERROR:\s*', ''
        $exactJsonDeploymentNotFound = $false
        try {
            $jsonError = $jsonDiagnostic | ConvertFrom-Json -ErrorAction Stop
            $expectedNotFoundMessage = "Deployment '$DeploymentName' could not be found."
            $exactJsonDeploymentNotFound =
                $jsonError.error.code -ceq 'DeploymentNotFound' -and
                $jsonError.error.message -ceq $expectedNotFoundMessage
        } catch {
            $exactJsonDeploymentNotFound = $false
        }
        if ($AllowNotFound -and
            ($diagnostic -match $deploymentNotFoundPattern -or $exactJsonDeploymentNotFound)) {
            if (-not $deploymentObserved) {
                $notFoundObservations++
                if ($notFoundObservations -ge $requiredNotFoundObservations) {
                    Write-Output "Deployment '$DeploymentName' was not submitted after $notFoundObservations consecutive observations."
                    return
                }
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                if ($deploymentObserved) {
                    throw "Deployment '$DeploymentName' was observed but did not finish within $TimeoutSeconds seconds."
                }
                throw "Deployment '$DeploymentName' absence could not be proven within $TimeoutSeconds seconds."
            }
            Start-Sleep -Seconds $PollIntervalSeconds
            continue
        }
        throw "Unable to read deployment '$DeploymentName' in resource group '$ResourceGroup'."
    }

    $notFoundObservations = 0
    $deploymentObserved = $true
    $deployment = $deploymentJson | ConvertFrom-Json
    switch ($deployment.provisioningState) {
        'Succeeded' {
            if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
                'complete=true' | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
            }
            Write-Output "Deployment '$DeploymentName' succeeded."
            return
        }
        { $_ -in @('Canceled', 'Failed') } {
            if ($AllowFailedTerminalState) {
                Write-Output "Deployment '$DeploymentName' reached terminal state '$($_)'."
                return
            }
            $errorDetail = if ($null -eq $deployment.error) {
                'Azure returned no error detail.'
            } else {
                $deployment.error | ConvertTo-Json -Depth 20 -Compress
            }
            throw "Deployment '$DeploymentName' ended in state '$($_)': $errorDetail"
        }
    }

    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        if ($AllowTimeout) {
            if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
                'complete=false' | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
            }
            Write-Output "Deployment '$DeploymentName' is still '$($deployment.provisioningState)'; refresh Azure login before polling again."
            return
        }

        throw "Deployment '$DeploymentName' did not finish within $TimeoutSeconds seconds."
    }

    Start-Sleep -Seconds $PollIntervalSeconds
}
