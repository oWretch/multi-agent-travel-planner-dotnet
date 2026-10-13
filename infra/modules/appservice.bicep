param planName string
param webAppName string
param location string
param tags object = {}

param appInsightsConnectionString string
param foundryProjectEndpoint string
param openAiEndpoint string
param openAiDeployment string
param modelRouterDeployment string

@description('Stable agent IDs hosted in the app. Each entry becomes an Agents__<id>__Id application setting.')
param agents array

@description('Optional per-agent deployment overrides keyed by logical agent name.')
param agentDeployments object = {}

var agentIdAppSettings = [
  for agent in agents: {
    name: 'Agents__${agent}__Id'
    value: agent
  }
]

var agentDeploymentAppSettings = [
  for agentDeployment in items(agentDeployments): {
    name: 'Agents__${agentDeployment.key}__Deployment'
    value: agentDeployment.value
  }
]

var baseAppSettings = [
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: appInsightsConnectionString
  }
  {
    name: 'AzureAIProject__Endpoint'
    value: foundryProjectEndpoint
  }
  {
    name: 'Foundry__ProjectEndpoint'
    value: foundryProjectEndpoint
  }
  {
    name: 'FOUNDRY_PROJECT_ENDPOINT'
    value: foundryProjectEndpoint
  }
  {
    name: 'AzureOpenAI__Endpoint'
    value: openAiEndpoint
  }
  {
    name: 'AzureOpenAI__Deployment'
    value: openAiDeployment
  }
  {
    name: 'AzureOpenAI__ModelRouterDeployment'
    value: modelRouterDeployment
  }
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Production'
  }
  // Instrumentation is configured in code. Disable the App Service
  // codeless attach so both pipelines don't subscribe to the same sources.
  {
    name: 'ApplicationInsightsAgent_EXTENSION_VERSION'
    value: 'disabled'
  }
]

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'P0v3'
    tier: 'Premium0V3'
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: concat(baseAppSettings, agentIdAppSettings, agentDeploymentAppSettings)
    }
  }
}

output webAppName string = webApp.name
output webAppUri string = 'https://${webApp.properties.defaultHostName}'
output principalId string = webApp.identity.principalId
