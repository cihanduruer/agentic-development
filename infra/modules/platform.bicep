param environment string
param location string
param sqlEntraAdminLogin string
param sqlEntraAdminObjectId string
param tenantId string
param deploymentPrincipalObjectId string
param operationsApiAudience string
param tags object

var suffix = uniqueString(subscription().id, resourceGroup().id, environment)
var baseName = 'ahb-${environment}-${suffix}'
var routingDeploymentName = 'gpt-4.1-mini'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${baseName}-log'
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-appi'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource aiServices 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: '${baseName}-ai'
  location: 'swedencentral'
  kind: 'AIServices'
  tags: tags
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: '${baseName}-ai'
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource routingModel 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: aiServices
  name: routingDeploymentName
  sku: {
    name: 'GlobalStandard'
    capacity: 10
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-4.1-mini'
      version: '2025-04-14'
    }
    raiPolicyName: 'Microsoft.Default'
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: '${baseName}-plan'
  location: location
  tags: tags
  sku: {
    name: 'B1'
    tier: 'Basic'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: '${baseName}-web'
  location: location
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {}
}

resource search 'Microsoft.Search/searchServices@2025-05-01' = {
  name: '${baseName}-search'
  location: location
  tags: tags
  sku: {
    name: 'basic'
  }
  properties: {
    disableLocalAuth: true
    hostingMode: 'Default'
    publicNetworkAccess: 'enabled'
    replicaCount: 1
    partitionCount: 1
    semanticSearch: 'free'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${baseName}-sql'
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Application'
      login: sqlEntraAdminLogin
      sid: sqlEntraAdminObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: 'Disabled'
  }
}

resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: 'hotelbooking'
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    readScale: 'Disabled'
    zoneRedundant: false
  }
}

resource api 'Microsoft.Web/sites@2023-12-01' = {
  name: '${baseName}-api'
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
      appSettings: [
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'ConnectionStrings__HotelBooking'
          value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${database.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
        }
        {
          name: 'Database__ApplyMigrations'
          value: 'false'
        }
        {
          name: 'AllowedOrigins__0'
          value: 'https://${staticWebApp.properties.defaultHostname}'
        }
        {
          name: 'MicrosoftRouting__ModelEnabled'
          value: 'true'
        }
        {
          name: 'MicrosoftRouting__Endpoint'
          value: 'https://${aiServices.name}.openai.azure.com/'
        }
        {
          name: 'MicrosoftRouting__Deployment'
          value: routingModel.name
        }
        {
          name: 'RoutingEvaluation__Enabled'
          value: 'true'
        }
        {
          name: 'RoutingEvaluation__ContentSafetyEndpoint'
          value: 'https://${aiServices.name}.cognitiveservices.azure.com/'
        }
        {
          name: 'RoutingEvaluation__SearchEndpoint'
          value: 'https://${search.name}.search.windows.net/'
        }
        {
          name: 'RoutingEvaluation__SearchIndex'
          value: 'knowledge'
        }
        {
          name: 'OperationsAuth__Authority'
          value: '${az.environment().authentication.loginEndpoint}${subscription().tenantId}/v2.0'
        }
        {
          name: 'OperationsAuth__Audience'
          value: operationsApiAudience
        }
        {
          name: 'OperationsAuth__RequiredRole'
          value: 'Operations.Ingest'
        }
        {
          name: 'OperationsEvents__RetentionDays'
          value: '30'
        }
        {
          name: 'OperationsEvents__MaxRecords'
          value: '2000'
        }
        {
          name: 'OperationsEvents__MaxQueryLimit'
          value: '500'
        }
      ]
    }
  }
}

resource apiOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, api.id, 'cognitive-services-openai-user')
  scope: aiServices
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
  }
}

resource deploymentOpenAiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (environment == 'dev') {
  name: guid(aiServices.id, deploymentPrincipalObjectId, 'deployment-cognitive-services-openai-user')
  scope: aiServices
  properties: {
    principalId: deploymentPrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
    )
  }
}

resource apiContentSafetyRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, api.id, 'cognitive-services-user')
  scope: aiServices
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'a97b65f3-24c7-4388-baec-2e87135dc908'
    )
  }
}

resource apiSearchReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, api.id, 'search-index-data-reader')
  scope: search
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '1407120a-92aa-4202-b7e9-c0e197c71c8f'
    )
  }
}

resource deploymentSearchContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, deploymentPrincipalObjectId, 'search-index-data-contributor')
  scope: search
  properties: {
    principalId: deploymentPrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
    )
  }
}

resource deploymentSearchServiceContributorRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, deploymentPrincipalObjectId, 'search-service-contributor')
  scope: search
  properties: {
    principalId: deploymentPrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
    )
  }
}

output apiUrl string = 'https://${api.properties.defaultHostName}'
output apiAppName string = api.name
output apiName string = api.name
output apiPrincipalId string = api.identity.principalId
output aiServicesEndpoint string = 'https://${aiServices.name}.openai.azure.com/'
output routingDeploymentName string = routingModel.name
output staticWebAppName string = staticWebApp.name
output searchServiceName string = search.name
output searchEndpoint string = 'https://${search.name}.search.windows.net/'
output sqlServerName string = sqlServer.name
output legacyKeyVaultName string = '${baseName}-kv'
