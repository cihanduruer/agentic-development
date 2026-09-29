targetScope = 'subscription'

@description('Azure region for the target environment.')
param location string = 'westeurope'

@description('Resource group created for the target environment.')
param resourceGroupName string = 'agentic-hotelbookingdev'

@description('Deployment environment tag.')
param environment string = 'dev'

@secure()
@description('SQL administrator password stored in Key Vault.')
param sqlAdminPassword string

@description('SQL administrator login.')
param sqlAdminLogin string = 'hoteladmin'

@description('Client ID of the Entra operations API registration, used as the v2 access-token audience.')
param operationsApiAudience string

var tags = {
  application: 'agentic-hotelbooking'
  environment: environment
  'managed-by': 'bicep'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module platform 'modules/platform.bicep' = {
  name: 'platform-${environment}'
  scope: resourceGroup
  params: {
    environment: environment
    location: location
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
    operationsApiAudience: operationsApiAudience
    tags: tags
  }
}

output resourceGroupName string = resourceGroup.name
output apiUrl string = platform.outputs.apiUrl
output apiAppName string = platform.outputs.apiAppName
output staticWebAppName string = platform.outputs.staticWebAppName
output searchServiceName string = platform.outputs.searchServiceName
output sqlServerName string = platform.outputs.sqlServerName
