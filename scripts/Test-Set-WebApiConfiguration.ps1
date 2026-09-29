$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "web-config-$([Guid]::NewGuid())"
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "appsettings.json") -Value '{"ApiBaseUrl":"http://localhost:5224/"}'
    Set-Content -LiteralPath (Join-Path $testRoot "appsettings.json.br") -Value "stale-brotli"
    Set-Content -LiteralPath (Join-Path $testRoot "appsettings.json.gz") -Value "stale-gzip"

    & "$PSScriptRoot/Set-WebApiConfiguration.ps1" `
        -WebRoot $testRoot `
        -ApiBaseUrl "https://api.example.test"

    $configuration = Get-Content -LiteralPath (Join-Path $testRoot "appsettings.json") -Raw | ConvertFrom-Json
    Assert-True `
        -Condition ($configuration.ApiBaseUrl -eq "https://api.example.test/") `
        -Message "The deployed API URL should be normalized and written."
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $testRoot "appsettings.json.br"))) `
        -Message "The stale Brotli configuration must be removed."
    Assert-True `
        -Condition (-not (Test-Path -LiteralPath (Join-Path $testRoot "appsettings.json.gz"))) `
        -Message "The stale gzip configuration must be removed."

    $staticWebAppsConfiguration = Get-Content `
        -LiteralPath (Join-Path $PSScriptRoot "../src/Web/wwwroot/staticwebapp.config.json") `
        -Raw |
        ConvertFrom-Json
    Assert-True `
        -Condition ($staticWebAppsConfiguration.navigationFallback.rewrite -eq "/index.html") `
        -Message "Static Web Apps must rewrite client-side routes to /index.html."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
