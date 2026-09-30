targetScope = 'resourceGroup'

param principalId string
param searchServiceName string

resource search 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: searchServiceName
}

resource searchReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, principalId, 'knowledge-mcp-search-index-data-reader')
  scope: search
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '1407120a-92aa-4202-b7e9-c0e197c71c8f'
    )
  }
}
