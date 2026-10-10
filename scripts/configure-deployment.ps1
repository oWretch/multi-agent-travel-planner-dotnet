$ErrorActionPreference = 'Stop'

function Get-DeploymentValue([string] $Name) {
    $value = azd env get-value $Name
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        throw "Missing $Name. Run 'azd provision' first."
    }
    return $value.Trim()
}

$endpoint = Get-DeploymentValue 'AZURE_OPENAI_ENDPOINT'
$deployment = Get-DeploymentValue 'AZURE_OPENAI_DEPLOYMENT'
$router = Get-DeploymentValue 'AZURE_OPENAI_MODEL_ROUTER_DEPLOYMENT'
$stateless = Get-DeploymentValue 'AZURE_AI_GATEWAY_STATELESS_RESPONSES'
$webApp = Get-DeploymentValue 'WEB_APP_NAME'
$resourceGroup = Get-DeploymentValue 'AZURE_RESOURCE_GROUP'
$subscription = Get-DeploymentValue 'AZURE_SUBSCRIPTION_ID'
if ($stateless -ne 'true') {
    throw "Invalid AI gateway deployment outputs. Run 'azd provision' first."
}

az webapp config appsettings set --name $webApp --resource-group $resourceGroup `
    --subscription $subscription `
    --settings "AzureOpenAI__Endpoint=$endpoint" "AzureOpenAI__Deployment=$deployment" `
    "AzureOpenAI__ModelRouterDeployment=$router" "AIGateway__UseStatelessResponses=$stateless" `
    --output none
if ($LASTEXITCODE -ne 0) {
    throw 'Could not update App Service model settings.'
}

$values = azd env get-values
if ($LASTEXITCODE -ne 0) {
    throw 'Could not export the local environment.'
}
New-Item -ItemType Directory -Path '.vscode' -Force | Out-Null
$values | Set-Content -Path '.vscode/.env' -Encoding utf8
Write-Host 'Updated App Service model settings and .vscode/.env from the selected azd environment.'
