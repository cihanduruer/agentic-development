[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ApiUrl,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Audience,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ResourceGroup,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$AppName
)

$ErrorActionPreference = 'Stop'
$api = $ApiUrl.TrimEnd('/')
$correlationId = "deployment-smoke-$([Guid]::NewGuid().ToString('N'))"

function Invoke-ApiRequest {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('GET', 'POST')]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$Uri,

        [string]$Body,

        [hashtable]$Headers = @{}
    )

    $parameters = @{
        Method               = $Method
        Uri                  = $Uri
        Headers              = $Headers
        SkipHttpErrorCheck   = $true
        MaximumRetryCount    = 2
        RetryIntervalSec     = 2
        OperationTimeoutSeconds = 30
    }
    if ($Method -eq 'POST') {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body
    }

    Invoke-WebRequest @parameters
}

function Assert-Status {
    param(
        [Parameter(Mandatory)]
        [Microsoft.PowerShell.Commands.WebResponseObject]$Response,

        [Parameter(Mandatory)]
        [int]$Expected,

        [Parameter(Mandatory)]
        [string]$Operation
    )

    if ([int]$Response.StatusCode -ne $Expected) {
        throw "$Operation returned HTTP $([int]$Response.StatusCode); expected $Expected."
    }
}

$eventBody = @{
    kind                 = 0
    correlationId        = $correlationId
    workItemId           = 'deployment-smoke'
    agent                = 'github-oidc-deployment'
    summary              = 'Verified authenticated operations ingestion after development deployment.'
    decision             = ''
    outcome              = 'accepted'
    confidence           = $null
    durationMilliseconds = 0
    knowledgeRevision    = $env:GITHUB_SHA
} | ConvertTo-Json -Compress

$routeBody = @{
    correlationId      = "$correlationId-route"
    workItemId         = 'deployment-smoke'
    taskCategory       = 'quality-assurance'
    requiredCapability = 'api-testing'
    risk               = 'reversible'
    evidenceComplete   = $true
    availableWorkers   = @{
        'qa-agent' = 'Runs API tests.'
    }
    knowledgeRevision  = $env:GITHUB_SHA
} | ConvertTo-Json -Compress

$publicRead = Invoke-ApiRequest -Method GET -Uri "$api/api/operations/events?limit=1"
Assert-Status -Response $publicRead -Expected 200 -Operation 'Public operations read'

$anonymousEvent = Invoke-ApiRequest -Method POST -Uri "$api/api/operations/events" -Body $eventBody
Assert-Status -Response $anonymousEvent -Expected 401 -Operation 'Anonymous operations ingestion'

$anonymousRoute = Invoke-ApiRequest -Method POST -Uri "$api/api/orchestration/route" -Body $routeBody
Assert-Status -Response $anonymousRoute -Expected 401 -Operation 'Anonymous orchestration routing'

$token = az account get-access-token `
    --resource $Audience `
    --query accessToken `
    --output tsv `
    --only-show-errors
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
    throw 'Azure CLI did not return an operations API access token.'
}

$token = $token.Trim()
if ($env:GITHUB_ACTIONS -eq 'true') {
    Write-Output "::add-mask::$token"
}
$authorizedHeaders = @{ Authorization = "Bearer $token" }
$authorizedEvent = Invoke-ApiRequest `
    -Method POST `
    -Uri "$api/api/operations/events" `
    -Body $eventBody `
    -Headers $authorizedHeaders
Assert-Status -Response $authorizedEvent -Expected 201 -Operation 'Authorized operations ingestion'
$token = $null
$authorizedHeaders.Clear()

az webapp restart `
    --resource-group $ResourceGroup `
    --name $AppName `
    --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Development App Service restart failed.'
}

$healthy = $false
for ($attempt = 1; $attempt -le 24; $attempt++) {
    Start-Sleep -Seconds 5
    try {
        $health = Invoke-ApiRequest -Method GET -Uri "$api/health"
        if ([int]$health.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    }
    catch {
        Write-Verbose "Health probe attempt $attempt failed: $($_.Exception.Message)"
    }
}
if (-not $healthy) {
    throw 'Development API did not become healthy within two minutes after restart.'
}

$persistedResponse = Invoke-ApiRequest -Method GET -Uri "$api/api/operations/events?limit=500"
Assert-Status -Response $persistedResponse -Expected 200 -Operation 'Post-restart operations read'
$persistedEvents = @($persistedResponse.Content | ConvertFrom-Json)
if (-not ($persistedEvents | Where-Object correlationId -EQ $correlationId)) {
    throw "Authorized event '$correlationId' was not persisted across the App Service restart."
}

Write-Output "Development operations smoke check passed for correlation '$correlationId'."
