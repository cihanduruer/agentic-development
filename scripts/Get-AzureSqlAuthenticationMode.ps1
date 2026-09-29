param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroup,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ServerName
)

$ErrorActionPreference = 'Stop'

$result = @(
    az sql server ad-only-auth get `
        --resource-group $ResourceGroup `
        --name $ServerName `
        --query azureAdOnlyAuthentication `
        --output tsv `
        --only-show-errors 2>&1
)
$exitCode = $LASTEXITCODE
$value = (($result | ForEach-Object { $_.ToString() }) -join "`n").Trim()

if ($exitCode -ne 0) {
    throw "Unable to read the Azure SQL Entra-only authentication child resource: $value"
}

switch ($value.ToLowerInvariant()) {
    'true' {
        Write-Output 'entraOnly'
        exit 0
    }
    'false' {
        Write-Output 'legacy'
        exit 0
    }
    default {
        throw "Azure SQL returned an invalid Entra-only authentication value '$value'."
    }
}
