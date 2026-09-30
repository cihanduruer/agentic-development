[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9._()-]+$')][string] $ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9-]+$')][string] $AppName,
    [Parameter(Mandatory)][guid] $ExpectedTenantId
)

$ErrorActionPreference = 'Stop'

function Read-IdentityGuid {
    param([object] $Value, [string] $Label)
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse([string] $Value, [ref] $parsed) -or $parsed -eq [guid]::Empty) {
        throw "The App Service SQL identity has an invalid $Label."
    }
    return $parsed
}

$account = az account show --query '{id:id,tenantId:tenantId}' --output json --only-show-errors |
    ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $account) {
    throw 'Unable to read the authenticated Azure account for SQL identity resolution.'
}
$subscriptionId = Read-IdentityGuid $account.id 'subscription ID'
$tenantId = Read-IdentityGuid $account.tenantId 'account tenant ID'
if ($tenantId -ne $ExpectedTenantId) {
    throw 'The authenticated Azure account does not match the expected tenant.'
}
$resourceId = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.Web/sites/$AppName"
$app = az webapp show --resource-group $ResourceGroup --name $AppName `
    --query '{id:id,name:name,type:type,identity:identity}' --output json --only-show-errors |
    ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $app) {
    throw 'Unable to read the exact App Service for SQL identity resolution.'
}
if ($app.id -ine $resourceId -or $app.name -ine $AppName -or $app.type -ine 'Microsoft.Web/sites' -or
    $app.identity.type -cne 'SystemAssigned' -or
    ($null -ne $app.identity.userAssignedIdentities -and
     @($app.identity.userAssignedIdentities.PSObject.Properties).Count -ne 0)) {
    throw 'The SQL target is not the exact App Service with only a system-assigned identity.'
}
$objectId = Read-IdentityGuid $app.identity.principalId 'App Service principal object ID'
if ((Read-IdentityGuid $app.identity.tenantId 'App Service tenant ID') -ne $tenantId) {
    throw 'The App Service identity does not match the expected tenant.'
}

# Project safe metadata at the CLI boundary; never print or follow clientSecretUrl.
$identity = az rest --method get `
    --url "https://management.azure.com$resourceId/providers/Microsoft.ManagedIdentity/identities/default?api-version=2024-11-30" `
    --query '{id:id,type:type,properties:{principalId:properties.principalId,clientId:properties.clientId,tenantId:properties.tenantId}}' `
    --output json --only-show-errors | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $identity) {
    throw 'Unable to resolve the exact App Service system-assigned identity through ARM; no fallback is permitted.'
}
$resolvedObjectId = Read-IdentityGuid $identity.properties.principalId 'ARM principal object ID'
$clientId = Read-IdentityGuid $identity.properties.clientId 'ARM application/client ID'
$resolvedTenantId = Read-IdentityGuid $identity.properties.tenantId 'ARM tenant ID'
if ($identity.id -ine $resourceId -or $identity.type -ine 'Microsoft.Web/sites' -or
    $resolvedObjectId -ne $objectId -or $resolvedTenantId -ne $tenantId -or $clientId -eq $objectId) {
    throw 'ARM system-assigned identity metadata does not match the exact App Service identity.'
}

[pscustomobject] @{
    ResourceId = $resourceId
    PrincipalName = $AppName
    PrincipalObjectId = $objectId.ToString('D')
    PrincipalClientId = $clientId.ToString('D')
    TenantId = $tenantId.ToString('D')
}
