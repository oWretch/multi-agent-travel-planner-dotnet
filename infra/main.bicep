targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment. Used as a resource name prefix.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Default Azure AI Foundry model deployment used by the coordinator and as the application fallback.')
param openAiModelName string = 'gpt-6-astra'

@description('Azure AI Foundry model version for the default deployment.')
param openAiModelVersion string = '2026-09-03'

@description('Capacity (TPM in thousands) for the default deployment.')
param openAiModelCapacity int = 10

@description('Additional Azure AI Foundry model deployments used by fixed routing and model router.')
param additionalOpenAiDeployments array = [
  {
    name: 'gpt-5.6-luna'
    modelName: 'gpt-5.6-luna'
    modelVersion: '2026-07-09'
    capacity: 10
  }
  {
    name: 'gpt-5.4-mini'
    modelName: 'gpt-5.4-mini'
    modelVersion: '2026-03-17'
    capacity: 10
  }
  {
    name: 'model-router'
    modelName: 'model-router'
    modelVersion: '2025-11-18'
    capacity: 10
    routing: {
      mode: 'balanced'
      models: [
        {
          format: 'OpenAI'
          name: 'gpt-5.4-mini'
          version: '2026-03-17'
        }
        {
          format: 'OpenAI'
          name: 'gpt-5.6-luna'
          version: '2026-07-09'
        }
        {
          format: 'OpenAI'
          name: 'gpt-5.6-sol'
          version: '2026-07-09'
        }
      ]
    }
  }
]

@description('Per-agent fixed deployment assignments keyed by logical agent name.')
param agentDeployments object = {
  Coordinator: 'gpt-6-astra'
  WeatherAdvisor: 'gpt-5.4-mini'
  CurrencyConverter: 'gpt-5.4-mini'
  BudgetOptimizer: 'gpt-5.6-luna'
  LocalKnowledge: 'gpt-5.4-mini'
  ItineraryPlanner: 'gpt-5.6-luna'
}

@description('Model router deployment used when a chat selects adaptive routing.')
param modelRouterDeployment string = 'model-router'

@description('Optional principal ID of the user/identity that will run the app locally. Granted Cognitive Services OpenAI User on the Foundry account so DefaultAzureCredential works without keys.')
param principalId string = ''

@description('Stable agent IDs hosted in the app. Each entry becomes an App Service application setting and an external agent registration in the Foundry project.')
param agents array = [
  'Coordinator'
  'WeatherAdvisor'
  'CurrencyConverter'
  'BudgetOptimizer'
  'LocalKnowledge'
  'ItineraryPlanner'
]

var abbrs = {
  appServicePlan: 'asp'
  webApp: 'app'
  appInsights: 'appi'
  logAnalytics: 'log'
  foundry: 'foundry'
}

var resourceToken = uniqueString(subscription().id, resourceGroup().id, environmentName)
var tags = {
  'azd-env-name': environmentName
  workload: 'multi-agent-travel-planner'
}

module logAnalytics 'modules/loganalytics.bicep' = {
  name: 'logAnalytics'
  params: {
    name: '${abbrs.logAnalytics}-${resourceToken}'
    location: location
    tags: tags
  }
}

module appInsights 'modules/appinsights.bicep' = {
  name: 'appInsights'
  params: {
    name: '${abbrs.appInsights}-${resourceToken}'
    location: location
    tags: tags
    workspaceId: logAnalytics.outputs.id
  }
}

module foundry 'modules/foundry.bicep' = {
  name: 'foundry'
  params: {
    name: '${abbrs.foundry}-${resourceToken}'
    projectName: 'travel-planner'
    appInsightsName: appInsights.outputs.name
    location: location
    tags: tags
    modelName: openAiModelName
    modelVersion: openAiModelVersion
    modelCapacity: openAiModelCapacity
    additionalModelDeployments: additionalOpenAiDeployments
    deployerPrincipalId: principalId
  }
}

module appService 'modules/appservice.bicep' = {
  name: 'appService'
  params: {
    planName: '${abbrs.appServicePlan}-${resourceToken}'
    webAppName: '${abbrs.webApp}-${resourceToken}'
    location: location
    tags: union(tags, { 'azd-service-name': 'web' })
    appInsightsConnectionString: appInsights.outputs.connectionString
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    openAiEndpoint: foundry.outputs.openAiEndpoint
    openAiDeployment: foundry.outputs.deploymentName
    agents: agents
    agentDeployments: agentDeployments
    modelRouterDeployment: modelRouterDeployment
  }
}

module roleAssignments 'modules/roles.bicep' = {
  name: 'roleAssignments'
  params: {
    openAiAccountName: foundry.outputs.name
    webAppPrincipalId: appService.outputs.principalId
    userPrincipalId: principalId
  }
}

output AZURE_LOCATION string = location
output WEB_APP_NAME string = appService.outputs.webAppName
output WEB_APP_URI string = appService.outputs.webAppUri
output APPLICATIONINSIGHTS_CONNECTION_STRING string = appInsights.outputs.connectionString
output AZURE_AI_FOUNDRY_ACCOUNT_NAME string = foundry.outputs.name
output AZURE_AI_FOUNDRY_PROJECT_NAME string = foundry.outputs.projectName
output AZURE_AI_FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output AZURE_OPENAI_ENDPOINT string = foundry.outputs.openAiEndpoint
output AZURE_OPENAI_DEPLOYMENT string = foundry.outputs.deploymentName
output AZURE_OPENAI_MODEL_ROUTER_DEPLOYMENT string = modelRouterDeployment
output AZURE_AI_AGENT_IDS string = join(agents, ',')
