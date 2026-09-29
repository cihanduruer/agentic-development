targetScope = 'resourceGroup'

@description('Azure region for the App Service.')
param location string = resourceGroup().location

@description('Name of the existing App Service plan.')
param appServicePlanName string

@description('Name of the API App Service whose system identity is bootstrapped before SQL cutover.')
param apiAppName string

@description('Deployment environment tag.')
param environment string

var tags = {
  application: 'agentic-hotelbooking'
  environment: environment
  'managed-by': 'bicep'
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' existing = {
  name: appServicePlanName
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: apiAppName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      linuxFxVersion: 'DOTNETCORE|10.0'
    }
  }
}

output apiPrincipalId string = api.identity.principalId
