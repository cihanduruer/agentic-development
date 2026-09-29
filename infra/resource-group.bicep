targetScope = 'resourceGroup'

@description('Azure region for the development environment.')
param location string = resourceGroup().location

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

module platform 'modules/platform.bicep' = {
  name: 'platform-${environment}'
  params: {
    environment: environment
    location: location
    sqlAdminLogin: sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
    operationsApiAudience: operationsApiAudience
    tags: tags
  }
}

output apiUrl string = platform.outputs.apiUrl
output apiAppName string = platform.outputs.apiAppName
output aiServicesEndpoint string = platform.outputs.aiServicesEndpoint
output routingDeploymentName string = platform.outputs.routingDeploymentName
output staticWebAppName string = platform.outputs.staticWebAppName
output searchServiceName string = platform.outputs.searchServiceName
output sqlServerName string = platform.outputs.sqlServerName
