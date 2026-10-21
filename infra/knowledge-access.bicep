param storageName string
param searchName string
param apiName string
param apiPrincipalId string
param migrationPrincipalId string
param existingSearchWriterAssignmentName string = ''

@allowed(['User', 'ServicePrincipal'])
param migrationPrincipalType string = 'ServicePrincipal'

resource storage 'Microsoft.Storage/storageAccounts@2025-01-01' existing = {
  name: storageName
}
resource search 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: searchName
}
resource api 'Microsoft.Web/sites@2023-12-01' existing = {
  name: apiName
}
resource apiStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, api.id, 'knowledge-blob-data-contributor')
  scope: storage
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
  }
}
resource apiSearchRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: empty(existingSearchWriterAssignmentName)
    ? guid(search.id, api.id, 'knowledge-search-data-contributor')
    : existingSearchWriterAssignmentName
  scope: search
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
    )
  }
}
resource apiSchemaRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, api.id, 'knowledge-search-service-contributor')
  scope: search
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
    )
  }
}
resource migrationStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, migrationPrincipalId, 'knowledge-blob-data-contributor')
  scope: storage
  properties: {
    principalId: migrationPrincipalId
    principalType: migrationPrincipalType
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
    )
  }
}
