param name string
param location string
param tags object = {}
param publisherEmail string
param openAiEndpoint string
param appInsightsName string
param webAppPrincipalId string
param developerPrincipalId string = ''
@minValue(0)
@maxValue(3600)
param cacheDurationSeconds int = 600

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource gateway 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    publisherName: 'Travel planner demo'
    publisherEmail: publisherEmail
    publicNetworkAccess: 'Enabled'
    virtualNetworkType: 'None'
  }
}

resource logger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: gateway
  name: 'applicationinsights'
  properties: {
    loggerType: 'applicationInsights'
    resourceId: appInsights.id
    credentials: {
      instrumentationKey: appInsights.properties.InstrumentationKey
    }
    isBuffered: true
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: gateway
  name: 'azure-openai'
  properties: {
    displayName: 'Travel planner AI gateway'
    apiRevision: '1'
    subscriptionRequired: false
    path: 'openai'
    protocols: ['https']
    serviceUrl: '${endsWith(openAiEndpoint, '/') ? substring(openAiEndpoint, 0, max(0, length(openAiEndpoint) - 1)) : openAiEndpoint}/openai'
  }
}

// Both paths are needed: fixed agents use Responses; model-router uses Chat Completions.
resource operations 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [
  for operation in [
    { name: 'responses', urlTemplate: '/responses' }
    { name: 'v1-responses', urlTemplate: '/v1/responses' }
    { name: 'chat-completions', urlTemplate: '/deployments/{deployment}/chat/completions' }
  ]: {
    parent: api
    name: operation.name
    properties: {
      displayName: operation.name
      method: 'POST'
      urlTemplate: operation.urlTemplate
      templateParameters: operation.name == 'chat-completions'
        ? [{ name: 'deployment', type: 'string', required: true }]
        : []
    }
  }
]

var allowedPrincipals = empty(developerPrincipalId)
  ? [webAppPrincipalId]
  : [webAppPrincipalId, developerPrincipalId]
var principalClaims = join(map(allowedPrincipals, principal => '<value>${principal}</value>'), '')
var policyTemplate = loadTextContent('policies/ai-gateway.xml')

resource policy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: replace(
      replace(
        replace(policyTemplate, '__TENANT_ID__', tenant().tenantId),
        '__PRINCIPAL_CLAIMS__',
        principalClaims
      ),
      '__CACHE_DURATION__',
      string(cacheDurationSeconds)
    )
  }
  dependsOn: [operations]
}

resource diagnostics 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  parent: api
  name: 'applicationinsights'
  properties: {
    loggerId: logger.id
    alwaysLog: 'allErrors'
    httpCorrelationProtocol: 'W3C'
    verbosity: 'information'
    logClientIp: false
    sampling: {
      samplingType: 'fixed'
      percentage: 100
    }
    frontend: {
      request: {
        headers: ['x-agent-name', 'x-routing-mode']
        body: { bytes: 0 }
      }
      response: {
        headers: ['x-ai-cache']
        body: { bytes: 0 }
      }
    }
    backend: {
      request: {
        headers: ['x-agent-name', 'x-routing-mode']
        body: { bytes: 0 }
      }
      response: {
        headers: []
        body: { bytes: 0 }
      }
    }
  }
}

output name string = gateway.name
output principalId string = gateway.identity.principalId
output endpoint string = gateway.properties.gatewayUrl
