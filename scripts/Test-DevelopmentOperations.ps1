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

        [hashtable]$Headers = @{},

        [ValidateRange(1, 60)]
        [int]$TimeoutSeconds = 30,

        [ValidateRange(0, 2)]
        [int]$RetryCount = 2
    )

    $parameters = @{
        Method               = $Method
        Uri                  = $Uri
        Headers              = $Headers
        SkipHttpErrorCheck   = $true
        OperationTimeoutSeconds = $TimeoutSeconds
    }
    if ($Method -eq 'POST') {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body
    }
    elseif ($RetryCount -gt 0) {
        $parameters.MaximumRetryCount = $RetryCount
        $parameters.RetryIntervalSec = 2
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
        'qa-agent'     = 'Runs API tests.'
        'human_review' = 'Reviews ambiguous or unsafe work.'
    }
    knowledgeRevision  = $env:GITHUB_SHA
} | ConvertTo-Json -Compress

$publicRead = Invoke-ApiRequest -Method GET -Uri "$api/api/operations/events?limit=1"
Assert-Status -Response $publicRead -Expected 200 -Operation 'Public operations read'
Write-Output 'Verified public operations read: HTTP 200.'

$anonymousEvent = Invoke-ApiRequest -Method POST -Uri "$api/api/operations/events" -Body $eventBody
Assert-Status -Response $anonymousEvent -Expected 401 -Operation 'Anonymous operations ingestion'
Write-Output 'Verified anonymous operations ingestion rejection: HTTP 401.'

$anonymousRoute = Invoke-ApiRequest -Method POST -Uri "$api/api/orchestration/route" -Body $routeBody
Assert-Status -Response $anonymousRoute -Expected 401 -Operation 'Anonymous orchestration routing'
Write-Output 'Verified anonymous orchestration routing rejection: HTTP 401.'

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
Write-Output "Verified OIDC-authorized operations ingestion: HTTP 201 for '$correlationId'."
$token = $null
$authorizedHeaders.Clear()

$subscriptionId = az account show --query id --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($subscriptionId)) {
    throw 'Azure CLI did not return the active subscription ID.'
}

$restartUri = '/subscriptions/{0}/resourceGroups/{1}/providers/Microsoft.Web/sites/{2}/restart?api-version=2024-11-01&synchronous=true' -f `
    [Uri]::EscapeDataString($subscriptionId.Trim()), `
    [Uri]::EscapeDataString($ResourceGroup), `
    [Uri]::EscapeDataString($AppName)
az rest `
    --method post `
    --uri $restartUri `
    --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Synchronous development App Service restart failed.'
}
Write-Output 'Verified synchronous App Service restart completion.'

$healthy = $false
$restartDeadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
$attempt = 0
while ([DateTimeOffset]::UtcNow -lt $restartDeadline) {
    $attempt++
    $remainingSeconds = [Math]::Ceiling(($restartDeadline - [DateTimeOffset]::UtcNow).TotalSeconds)
    $requestTimeout = [Math]::Max(1, [Math]::Min(10, $remainingSeconds))
    try {
        $health = Invoke-ApiRequest `
            -Method GET `
            -Uri "$api/health" `
            -TimeoutSeconds $requestTimeout `
            -RetryCount 0
        if ([int]$health.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    }
    catch {
        Write-Verbose "Health probe attempt $attempt failed: $($_.Exception.Message)"
    }

    $sleepSeconds = [Math]::Min(5, ($restartDeadline - [DateTimeOffset]::UtcNow).TotalSeconds)
    if ($sleepSeconds -gt 0) {
        Start-Sleep -Seconds $sleepSeconds
    }
}
if (-not $healthy) {
    throw 'Development API did not become healthy within two minutes after restart.'
}
Write-Output 'Verified post-restart API health recovery: HTTP 200.'

$persistedResponse = Invoke-ApiRequest -Method GET -Uri "$api/api/operations/events?limit=500"
Assert-Status -Response $persistedResponse -Expected 200 -Operation 'Post-restart operations read'
$persistedEvents = @($persistedResponse.Content | ConvertFrom-Json)
if (-not ($persistedEvents | Where-Object correlationId -EQ $correlationId)) {
    throw "Authorized event '$correlationId' was not persisted across the App Service restart."
}

Write-Output "Verified post-restart event persistence for correlation '$correlationId'."
Write-Output 'Development operations smoke check passed.'
