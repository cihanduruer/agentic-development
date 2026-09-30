targetScope = 'resourceGroup'

@secure()
param accessToken string

param location string = 'westeurope'

var tags = {
  application: 'agentic-hotelbooking-knowledge-mcp'
  environment: 'dev'
  'managed-by': 'bicep'
}

resource search 'Microsoft.Search/searchServices@2025-05-01' existing = {
  name: 'ahb-dev-bj5rmi3w3ntgq-search'
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'ahb-dev-knowledge-mcp-f1-plan'
  location: location
  tags: tags
  sku: {
    name: 'F1'
    tier: 'Free'
    size: 'F1'
    family: 'F'
    capacity: 1
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: 'ahb-dev-knowledge-mcp'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: false
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      linuxFxVersion: 'DOTNETCORE|10.0'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'KnowledgeMcp__SearchEndpoint'
          value: 'https://${search.name}.search.windows.net/'
        }
        {
          name: 'KnowledgeMcp__AccessToken'
          value: accessToken
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
        {
          name: 'SCM_DO_BUILD_DURING_DEPLOYMENT'
          value: 'false'
        }
      ]
    }
  }
}

module searchReaderRole 'modules/knowledge-mcp-search-role.bicep' = {
  name: 'knowledge-mcp-search-reader'
  params: {
    principalId: app.identity.principalId
    searchServiceName: search.name
  }
}

output appName string = app.name
output appUrl string = 'https://${app.properties.defaultHostName}'
output searchEndpoint string = 'https://${search.name}.search.windows.net/'
