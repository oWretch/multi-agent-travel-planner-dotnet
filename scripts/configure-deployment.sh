#!/bin/sh
set -eu

# azd preloads provisioning outputs into the hook environment. Fall back to the
# environment file so the script also works when run directly.
env_value() {
  eval "resolved=\${$1:-}"
  if [ -z "$resolved" ]; then
    resolved=$(azd env get-value "$1" 2>/dev/null || true)
  fi
  printf '%s' "$resolved"
}

endpoint=$(env_value AZURE_OPENAI_ENDPOINT)
deployment=$(env_value AZURE_OPENAI_DEPLOYMENT)
router=$(env_value AZURE_OPENAI_MODEL_ROUTER_DEPLOYMENT)
stateless=$(env_value AZURE_AI_GATEWAY_STATELESS_RESPONSES)
web_app=$(env_value WEB_APP_NAME)
resource_group=$(env_value AZURE_RESOURCE_GROUP)
subscription=$(env_value AZURE_SUBSCRIPTION_ID)

if [ -z "$endpoint" ] || [ -z "$deployment" ] || [ -z "$router" ] \
  || [ "$stateless" != 'true' ] || [ -z "$web_app" ] || [ -z "$resource_group" ] || [ -z "$subscription" ]; then
  echo "AI gateway deployment outputs are missing or invalid. Run 'azd provision' first." >&2
  exit 1
fi

az webapp config appsettings set --name "$web_app" --resource-group "$resource_group" \
  --subscription "$subscription" \
  --settings "AzureOpenAI__Endpoint=$endpoint" "AzureOpenAI__Deployment=$deployment" \
  "AzureOpenAI__ModelRouterDeployment=$router" "AIGateway__UseStatelessResponses=$stateless" \
  --output none

mkdir -p .vscode
azd env get-values > .vscode/.env
echo "Updated App Service model settings and .vscode/.env from the selected azd environment."
