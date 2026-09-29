[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$WebRoot,

    [Parameter(Mandatory)]
    [string]$ApiBaseUrl
)

$ErrorActionPreference = "Stop"

$uri = [Uri]$ApiBaseUrl
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne "https" -or -not [string]::IsNullOrEmpty($uri.Query) -or -not [string]::IsNullOrEmpty($uri.Fragment)) {
    throw "ApiBaseUrl must be an absolute HTTPS URL without a query or fragment."
}

$normalizedApiBaseUrl = $uri.AbsoluteUri.TrimEnd("/") + "/"
$configurationPath = Join-Path $WebRoot "appsettings.json"
if (-not (Test-Path -LiteralPath $WebRoot -PathType Container)) {
    throw "Web root '$WebRoot' does not exist."
}

@{ ApiBaseUrl = $normalizedApiBaseUrl } |
    ConvertTo-Json |
    Set-Content -LiteralPath $configurationPath -Encoding utf8

@("$configurationPath.br", "$configurationPath.gz") |
    Where-Object { Test-Path -LiteralPath $_ } |
    Remove-Item -Force

$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
if ($configuration.ApiBaseUrl -ne $normalizedApiBaseUrl) {
    throw "The generated web configuration does not contain the expected API URL."
}

$staleVariants = @("$configurationPath.br", "$configurationPath.gz") |
    Where-Object { Test-Path -LiteralPath $_ }
if ($staleVariants.Count -ne 0) {
    throw "Precompressed runtime configuration variants remain: $($staleVariants -join ', ')."
}
