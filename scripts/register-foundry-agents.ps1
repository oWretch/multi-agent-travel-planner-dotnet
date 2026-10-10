# Registers every agent hosted in the App Service as an external agent in the
# Foundry project. Foundry matches traces to a registration by comparing the
# gen_ai.agent.id attribute on incoming spans with the registration's
# otel_agent_id, so the IDs here must match the Agents__<Name>__Id application
# settings the app reads at startup.
#
# Runs from the azd postdeploy hook and is safe to run repeatedly: an agent that
# is already registered is left untouched.

#Requires -Version 7
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$apiVersion = 'v1'
$previewFeature = 'ExternalAgents=V1Preview'
$tokenScope = 'https://ai.azure.com/.default'
$description = 'Agent of the multi-agent travel planner hosted on Azure App Service.'

# azd preloads outputs from main.bicep into the hook environment. Fall back to
# the environment file so the script also works when run directly.
function Get-EnvValue([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = (azd env get-value $Name 2>$null)
    }
    return ($value | Out-String).Trim()
}

$projectEndpoint = Get-EnvValue 'AZURE_AI_FOUNDRY_PROJECT_ENDPOINT'
$agentIds = Get-EnvValue 'AZURE_AI_AGENT_IDS'

if ([string]::IsNullOrWhiteSpace($projectEndpoint)) {
    throw "AZURE_AI_FOUNDRY_PROJECT_ENDPOINT is not set. Run 'azd provision' first."
}

if ([string]::IsNullOrWhiteSpace($agentIds)) {
    throw "AZURE_AI_AGENT_IDS is not set. Run 'azd provision' first."
}

$projectEndpoint = $projectEndpoint.TrimEnd('/')

$token = (azd auth token --scope $tokenScope --output json | ConvertFrom-Json).token
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Could not acquire a token for $tokenScope. Run 'azd auth login'."
}

$headers = @{
    Authorization     = "Bearer $token"
    'Foundry-Features' = $previewFeature
}

Write-Host "Registering external agents in $projectEndpoint"

$failures = 0

foreach ($agentId in $agentIds.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $agentId = $agentId.Trim()
    if (-not $agentId) { continue }

    $existing = $null
    try {
        $existing = Invoke-RestMethod -Method Get -Headers $headers `
            -Uri "$projectEndpoint/agents/$agentId`?api-version=$apiVersion"
    }
    catch {
        if ($_.Exception.Response.StatusCode -ne [System.Net.HttpStatusCode]::NotFound) {
            Write-Error "  ${agentId}: lookup failed - $($_.Exception.Message)" -ErrorAction Continue
            $failures++
            continue
        }
    }

    if ($existing) {
        Write-Host "  ${agentId}: already registered"
        continue
    }

    $body = @{
        name        = $agentId
        description = $description
        definition  = @{
            kind          = 'external'
            otel_agent_id = $agentId
        }
    } | ConvertTo-Json -Depth 5

    try {
        Invoke-RestMethod -Method Post -Headers $headers -ContentType 'application/json' `
            -Uri "$projectEndpoint/agents`?api-version=$apiVersion" -Body $body | Out-Null
        Write-Host "  ${agentId}: registered"
    }
    catch {
        Write-Error "  ${agentId}: registration failed - $($_.Exception.Message)" -ErrorAction Continue
        $failures++
    }
}

if ($failures -gt 0) {
    throw "$failures agent registration(s) failed. External agents are a Foundry preview feature and need the Foundry User role on the project."
}

Write-Host 'All agents are registered. Open the project in https://ai.azure.com and select Agents to view traces.'
