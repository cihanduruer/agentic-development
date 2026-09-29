targetScope = 'resourceGroup'

@description('Azure region for the development environment.')
param location string = resourceGroup().location

@description('Deployment environment tag.')
param environment string = 'dev'

@description('Display name of the Microsoft Entra principal that administers Azure SQL.')
param sqlEntraAdminLogin string

@description('Object ID of the Microsoft Entra principal that administers Azure SQL.')
param sqlEntraAdminObjectId string

@description('Tenant ID containing the SQL administrator and workload identities.')
param tenantId string = subscription().tenantId

@description('Object ID of the GitHub OIDC deployment principal used for knowledge indexing.')
param deploymentPrincipalObjectId string

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
    sqlEntraAdminLogin: sqlEntraAdminLogin
    sqlEntraAdminObjectId: sqlEntraAdminObjectId
    tenantId: tenantId
    deploymentPrincipalObjectId: deploymentPrincipalObjectId
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
output apiName string = platform.outputs.apiName
output apiPrincipalId string = platform.outputs.apiPrincipalId
output searchEndpoint string = platform.outputs.searchEndpoint
