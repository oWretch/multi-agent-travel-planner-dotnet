#!/bin/sh
# Registers every agent hosted in the App Service as an external agent in the
# Foundry project. Foundry matches traces to a registration by comparing the
# gen_ai.agent.id attribute on incoming spans with the registration's
# otel_agent_id, so the IDs here must match the Agents__<Name>__Id application
# settings the app reads at startup.
#
# Runs from the azd postdeploy hook and is safe to run repeatedly: an agent that
# is already registered is left untouched.

set -eu

API_VERSION='v1'
PREVIEW_FEATURE='ExternalAgents=V1Preview'
TOKEN_SCOPE='https://ai.azure.com/.default'
DESCRIPTION='Agent of the multi-agent travel planner hosted on Azure App Service.'

# azd preloads outputs from main.bicep into the hook environment. Fall back to
# the environment file so the script also works when run directly.
env_value() {
  eval "resolved=\${$1:-}"
  if [ -z "$resolved" ]; then
    resolved=$(azd env get-value "$1" 2>/dev/null || true)
  fi
  printf '%s' "$resolved"
}

project_endpoint=$(env_value AZURE_AI_FOUNDRY_PROJECT_ENDPOINT)
agent_ids=$(env_value AZURE_AI_AGENT_IDS)

if [ -z "$project_endpoint" ]; then
  echo "AZURE_AI_FOUNDRY_PROJECT_ENDPOINT is not set. Run 'azd provision' first." >&2
  exit 1
fi

if [ -z "$agent_ids" ]; then
  echo "AZURE_AI_AGENT_IDS is not set. Run 'azd provision' first." >&2
  exit 1
fi

project_endpoint=${project_endpoint%/}

token=$(azd auth token --scope "$TOKEN_SCOPE" --output json \
  | tr -d '\n' \
  | sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')

if [ -z "$token" ]; then
  echo "Could not acquire a token for $TOKEN_SCOPE. Run 'azd auth login'." >&2
  exit 1
fi

echo "Registering external agents in $project_endpoint"

failures=0
original_ifs=$IFS
IFS=','
for agent_id in $agent_ids; do
  IFS=$original_ifs

  status=$(curl -sS -o /dev/null -w '%{http_code}' \
    "$project_endpoint/agents/$agent_id?api-version=$API_VERSION" \
    -H "Authorization: Bearer $token" \
    -H "Foundry-Features: $PREVIEW_FEATURE")

  if [ "$status" = '200' ]; then
    echo "  $agent_id: already registered"
    IFS=','
    continue
  fi

  if [ "$status" != '404' ]; then
    echo "  $agent_id: lookup failed with HTTP $status" >&2
    failures=$((failures + 1))
    IFS=','
    continue
  fi

  body=$(printf '{"name":"%s","description":"%s","definition":{"kind":"external","otel_agent_id":"%s"}}' \
    "$agent_id" "$DESCRIPTION" "$agent_id")

  response=$(curl -sS -w '\n%{http_code}' \
    -X POST "$project_endpoint/agents?api-version=$API_VERSION" \
    -H "Authorization: Bearer $token" \
    -H "Foundry-Features: $PREVIEW_FEATURE" \
    -H 'Content-Type: application/json' \
    -d "$body")

  status=$(printf '%s' "$response" | tail -n 1)

  case "$status" in
    2??)
      echo "  $agent_id: registered"
      ;;
    *)
      echo "  $agent_id: registration failed with HTTP $status" >&2
      printf '%s\n' "$response" | sed '$d' >&2
      failures=$((failures + 1))
      ;;
  esac

  IFS=','
done
IFS=$original_ifs

if [ "$failures" -gt 0 ]; then
  echo "$failures agent registration(s) failed. External agents are a Foundry preview feature and need the Foundry User role on the project." >&2
  exit 1
fi

echo "All agents are registered. Open the project in https://ai.azure.com and select Agents to view traces."
