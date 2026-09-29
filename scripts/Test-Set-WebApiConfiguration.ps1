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

function Assert-Throws {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") {
            throw "Expected error containing '$ExpectedMessage', got '$($_.Exception.Message)'."
        }
        return
    }

    throw "Expected an error containing '$ExpectedMessage', but no error was thrown."
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "web-config-$([Guid]::NewGuid())"
try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $testRoot "appsettings.json") -Value '{"ApiBaseUrl":"http://localhost:5224/"}'
    Set-Content -LiteralPath (Join-Path $testRoot "appsettings.json.br") -Value "stale-brotli"
    Set-Content -LiteralPath (Join-Path $testRoot "appsettings.json.gz") -Value "stale-gzip"

    @(
        "http://api.example.test",
        "/api",
        "https://api.example.test?environment=dev",
        "https://api.example.test#configuration"
    ) | ForEach-Object {
        $invalidApiBaseUrl = $_
        Assert-Throws `
            -Action {
                & "$PSScriptRoot/Set-WebApiConfiguration.ps1" `
                    -WebRoot $testRoot `
                    -ApiBaseUrl $invalidApiBaseUrl
            } `
            -ExpectedMessage "ApiBaseUrl must be an absolute HTTPS URL without a query or fragment."
    }

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
