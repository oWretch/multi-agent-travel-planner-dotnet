param name string
param projectName string = '${name}-proj'
param appInsightsName string
param location string
param tags object = {}

param modelName string = 'gpt-4.1-mini'
param modelVersion string = '2025-04-14'
param modelCapacity int = 30
param deploymentName string = modelName

@description('Additional model deployments. Each item requires name, modelName, modelVersion, and capacity, with optional routing settings.')
param additionalModelDeployments array = []

@description('Optional principal ID of the identity running azd. Granted Foundry User on the project so the postdeploy hook can register external agents.')
param deployerPrincipalId string = ''

// Foundry User. Required to create external agent registrations on the project.
var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

// Log Analytics Reader + Privileged Monitoring Data Reader. The latter is
// required for the Foundry project to query GenAI trace content.
var appInsightsReaderRoleIds = [
  '73c42c96-874c-492b-b04d-ab87d138a893'
  'dbc9c667-e97f-4491-aee6-90b9cf960190'
]

var modelDeployments = concat(
  [
    {
      name: deploymentName
      modelFormat: 'OpenAI'
      modelName: modelName
      modelVersion: modelVersion
      capacity: modelCapacity
    }
  ],
  additionalModelDeployments
)

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: name
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: name
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
  }

  resource project 'projects' = {
    name: projectName
    location: location
    identity: {
      type: 'SystemAssigned'
    }
    properties: {}

    resource appInsightsConnection 'connections' = {
      name: '${projectName}-appinsights'
      properties: {
        category: 'AppInsights'
        target: appInsights.id
        authType: 'ApiKey'
        isSharedToAll: true
        credentials: {
          key: appInsights.properties.ConnectionString
        }
        metadata: {
          ApiType: 'Azure'
          ResourceId: appInsights.id
        }
      }
    }
  }

  resource accountAppInsightsConnection 'connections' = {
    name: '${name}-appinsights'
    properties: {
      category: 'AppInsights'
      target: appInsights.id
      authType: 'ApiKey'
      isSharedToAll: true
      credentials: {
        key: appInsights.properties.ConnectionString
      }
      metadata: {
        ApiType: 'Azure'
        ResourceId: appInsights.id
      }
    }
    dependsOn: [project]
  }
}

@batchSize(1)
resource deployments 'Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview' = [
  for modelDeployment in modelDeployments: {
    parent: account
    name: modelDeployment.name
    sku: {
      name: 'GlobalStandard'
      capacity: modelDeployment.capacity
    }
    properties: union(
      {
        model: {
          format: modelDeployment.?modelFormat ?? 'OpenAI'
          name: modelDeployment.modelName
          version: modelDeployment.modelVersion
        }
      },
      contains(modelDeployment, 'routing')
        ? {
            routing: modelDeployment.routing
          }
        : {}
    )
    dependsOn: [account::accountAppInsightsConnection]
  }
]

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource appInsightsReaderRolesAccount 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in appInsightsReaderRoleIds: {
    scope: appInsights
    name: guid(account.id, roleId, appInsights.id)
    properties: {
      principalId: account.identity.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]

resource appInsightsReaderRolesProject 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for roleId in appInsightsReaderRoleIds: {
    scope: appInsights
    name: guid(account::project.id, roleId, appInsights.id)
    properties: {
      principalId: account::project.identity.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
    }
  }
]

resource deployerFoundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  scope: account::project
  name: guid(account::project.id, deployerPrincipalId, foundryUserRoleId)
  properties: {
    principalId: deployerPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

resource projectManagedIdentityFoundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account
  name: guid(account.id, account::project.id, foundryUserRoleId)
  properties: {
    principalId: account::project.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

output name string = account.name
output projectName string = account::project.name
output projectEndpoint string = account::project.properties.endpoints['AI Foundry API']
output openAiEndpoint string = account.properties.endpoints['OpenAI Language Model Instance API']
output deploymentName string = deploymentName
