targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment. Used as a resource name prefix.')
param environmentName string

@minLength(1)
@description('Primary location for all resources.')
param location string

@description('Azure AI Foundry model to deploy.')
param openAiModelName string = 'gpt-5.6-luna'

@description('Azure AI Foundry model version.')
param openAiModelVersion string = '2026-07-09'

@description('Capacity (TPM in thousands) for the model deployment.')
param openAiModelCapacity int = 30

@description('Additional Azure AI Foundry model deployments. Each item requires name, modelName, modelVersion, and capacity.')
param additionalOpenAiDeployments array = []

@description('Optional per-agent deployment overrides keyed by logical agent name. Values must match deployed model names.')
param agentDeployments object = {}

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
output AZURE_AI_AGENT_IDS string = join(agents, ',')
