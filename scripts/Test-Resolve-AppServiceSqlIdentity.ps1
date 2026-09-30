$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Resolve-AppServiceSqlIdentity.ps1'
$tenantId = 'a01cc91b-6d33-4777-9c32-0b02b5cd3473'
$objectId = '3037fe2b-71f3-4322-9b80-e57c1e756a94'
$clientId = '13093b8a-f113-4f19-acef-f5e2d2243c86'
$subscriptionId = '11111111-2222-3333-4444-555555555555'
$resourceId = "/subscriptions/$subscriptionId/resourceGroups/test-rg/providers/Microsoft.Web/sites/test-api"

function global:az {
    $arguments = @($args | ForEach-Object { [string] $_ })
    $global:IdentityCalls.Add(($arguments -join ' '))
    $global:LASTEXITCODE = 0
    switch ($arguments[0]) {
        'account' { $global:IdentityAccount | ConvertTo-Json -Depth 8 }
        'webapp' { $global:IdentityApp | ConvertTo-Json -Depth 8 }
        'rest' {
            if ($global:IdentityFailure) {
                $global:LASTEXITCODE = 1
                return 'null'
            }
            $global:IdentityMetadata | ConvertTo-Json -Depth 8
        }
        default { throw 'Unexpected Azure CLI invocation.' }
    }
}

try {
    foreach ($mutation in @(
        'none', 'account-tenant', 'app-tenant', 'app-id', 'app-name', 'app-type',
        'identity-type', 'user-assigned', 'metadata-id', 'metadata-type',
        'object-id', 'tenant-id', 'invalid-client', 'empty-client', 'same-id', 'forbidden'
    )) {
        $global:IdentityCalls = [Collections.Generic.List[string]]::new()
        $global:IdentityFailure = $false
        $global:IdentityAccount = @{ id = $subscriptionId; tenantId = $tenantId }
        $global:IdentityApp = @{
            id = $resourceId; name = 'test-api'; type = 'Microsoft.Web/sites'
            identity = @{ type = 'SystemAssigned'; principalId = $objectId; tenantId = $tenantId }
        }
        $global:IdentityMetadata = @{
            id = $resourceId; type = 'Microsoft.Web/sites'
            properties = @{ principalId = $objectId; clientId = $clientId; tenantId = $tenantId }
        }
        switch ($mutation) {
            'account-tenant' { $global:IdentityAccount.tenantId = $subscriptionId }
            'app-tenant' { $global:IdentityApp.identity.tenantId = $subscriptionId }
            'app-id' { $global:IdentityApp.id += '/slots/other' }
            'app-name' { $global:IdentityApp.name = 'other' }
            'app-type' { $global:IdentityApp.type = 'Microsoft.ManagedIdentity/userAssignedIdentities' }
            'identity-type' { $global:IdentityApp.identity.type = 'SystemAssigned, UserAssigned' }
            'user-assigned' { $global:IdentityApp.identity.userAssignedIdentities = @{ other = @{} } }
            'metadata-id' { $global:IdentityMetadata.id += '/other' }
            'metadata-type' { $global:IdentityMetadata.type = 'Microsoft.Web/sites/slots' }
            'object-id' { $global:IdentityMetadata.properties.principalId = $subscriptionId }
            'tenant-id' { $global:IdentityMetadata.properties.tenantId = $subscriptionId }
            'invalid-client' { $global:IdentityMetadata.properties.clientId = 'not-a-guid' }
            'empty-client' { $global:IdentityMetadata.properties.clientId = [guid]::Empty.ToString() }
            'same-id' { $global:IdentityMetadata.properties.clientId = $objectId }
            'forbidden' { $global:IdentityFailure = $true }
        }
        $failure = $null
        $result = $null
        try {
            $result = & $scriptPath -ResourceGroup test-rg -AppName test-api -ExpectedTenantId $tenantId
        }
        catch { $failure = $_ }
        if ($mutation -eq 'none') {
            if ($failure) { throw $failure }
            if ($result.PrincipalObjectId -ne $objectId -or $result.PrincipalClientId -ne $clientId -or
                $result.TenantId -ne $tenantId -or $result.ResourceId -ne $resourceId) {
                throw 'Exact ARM binding did not preserve distinct object and client IDs.'
            }
            $expectedRest = "rest --method get --url https://management.azure.com$resourceId/providers/Microsoft.ManagedIdentity/identities/default?api-version=2024-11-30 --query {id:id,type:type,properties:{principalId:properties.principalId,clientId:properties.clientId,tenantId:properties.tenantId}} --output json --only-show-errors"
            if ($global:IdentityCalls.Count -ne 3 -or $global:IdentityCalls[2] -cne $expectedRest) {
                throw 'Resolver did not use the exact scoped ARM read and safe projection.'
            }
        }
        elseif (-not $failure -or $null -ne $result) {
            throw "Identity resolver accepted hostile state '$mutation'."
        }
    }
}
finally {
    Remove-Item Function:\global:az
    foreach ($name in @('IdentityCalls', 'IdentityFailure', 'IdentityAccount', 'IdentityApp', 'IdentityMetadata')) {
        Remove-Variable $name -Scope Global -ErrorAction SilentlyContinue
    }
}
Write-Output 'App Service SQL identity resolution passed: exact ARM binding and 15 rejection cases.'
exit 0
