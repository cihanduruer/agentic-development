targetScope = 'subscription'

@description('Azure region for the development environment.')
param location string = 'westeurope'

@description('Resource group created for the development environment.')
param resourceGroupName string = 'agentic-hotelbookingdev'

@description('Deployment environment tag.')
param environment string = 'dev'

@secure()
@description('SQL administrator password stored in Key Vault.')
param sqlAdminPassword string

@description('SQL administrator login.')
param sqlAdminLogin string = 'hoteladmin'

@description('Application ID URI exposed by the Entra operations API registration.')
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
output staticWebAppName string = platform.outputs.staticWebAppName
output searchServiceName string = platform.outputs.searchServiceName
output sqlServerName string = platform.outputs.sqlServerName
