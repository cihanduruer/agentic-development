param environment string
param location string
param sqlAdminLogin string
@secure()
param sqlAdminPassword string
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
          value: '@Microsoft.KeyVault(SecretUri=${sqlConnectionSecret.properties.secretUriWithVersion})'
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

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: '${baseName}-kv'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    publicNetworkAccess: 'Enabled'
    sku: {
      family: 'A'
      name: 'standard'
    }
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${baseName}-sql'
  location: location
  tags: tags
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
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

resource sqlConnectionSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'sql-connection-string'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${database.name};Persist Security Info=False;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
  }
}

resource apiSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, api.id, 'key-vault-secrets-user')
  scope: keyVault
  properties: {
    principalId: api.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6'
    )
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

output apiUrl string = 'https://${api.properties.defaultHostName}'
output aiServicesEndpoint string = 'https://${aiServices.name}.openai.azure.com/'
output routingDeploymentName string = routingModel.name
output staticWebAppName string = staticWebApp.name
output searchServiceName string = search.name
output sqlServerName string = sqlServer.name
