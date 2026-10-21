targetScope = 'resourceGroup'

param apiPrincipalId string
param environment string = 'dev'

var baseName = 'ahb-${environment}-${uniqueString(subscription().id, resourceGroup().id, environment)}'

resource email 'Microsoft.Communication/emailServices@2023-03-31' = {
  name: '${baseName}-email'
  location: 'global'
  properties: {
    dataLocation: 'Europe'
  }
}

resource domain 'Microsoft.Communication/emailServices/domains@2023-03-31' = {
  parent: email
  name: 'AzureManagedDomain'
  location: 'global'
  properties: {
    domainManagement: 'AzureManaged'
    userEngagementTracking: 'Disabled'
  }
}

resource communication 'Microsoft.Communication/communicationServices@2023-03-31' = {
  name: '${baseName}-mail'
  location: 'global'
  properties: {
    dataLocation: 'Europe'
    linkedDomains: [domain.id]
  }
}

// ACS Entra email authentication requires resource read/write, not a data-plane sender role.
resource senderRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'hotel-knowledge-email-sender')
  properties: {
    roleName: 'Hotel Knowledge Email Sender ${resourceGroup().name}'
    description: 'Vendor-required ACS email authentication permissions; assigned only on the dedicated mail resource.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Communication/CommunicationServices/Read'
          'Microsoft.Communication/CommunicationServices/Write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [resourceGroup().id]
  }
}

resource apiSenderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(communication.id, apiPrincipalId, 'hotel-knowledge-email-sender')
  scope: communication
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: senderRole.id
  }
}

output endpoint string = 'https://${communication.properties.hostName}/'
output senderAddress string = 'DoNotReply@${domain.properties.mailFromSenderDomain}'
