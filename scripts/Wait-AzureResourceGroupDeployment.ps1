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

    [switch] $AllowFailedTerminalState
)

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)

while ($true) {
    $deploymentJson = az deployment group show `
        --resource-group $ResourceGroup `
        --name $DeploymentName `
        --query properties `
        --output json `
        --only-show-errors

    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($deploymentJson)) {
        throw "Unable to read deployment '$DeploymentName' in resource group '$ResourceGroup'."
    }

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
