# Multi-agent travel planner on Azure App Service (.NET)

A minimal multi-agent ASP.NET Core sample that demonstrates how to monitor
[Microsoft Agent Framework][maf] agents hosted on
[Azure App Service](https://learn.microsoft.com/azure/app-service/) using
[OpenTelemetry][otel] and the **AI (preview) → Agents** experience in App
Service / Application Insights.

This sample is intentionally lighter than a production architecture so the
monitoring story is front and center: it has no Service Bus, no Cosmos DB, and
no background worker. Agents run in-process and conversation state is in memory.

This is the .NET version. Python, Node.js, and Java versions are tracked in
sibling Azure-Samples repos.

## Features

* Microsoft Agent Framework 1.3.0 multi-agent app: a **Coordinator** delegating
  to five specialists — **WeatherAdvisor**, **CurrencyConverter**,
  **BudgetOptimizer**, **LocalKnowledge**, and **ItineraryPlanner**.
* OpenTelemetry instrumented with the [GenAI semantic conventions][semconv] so
  each agent shows up separately in App Service → **AI (preview) → Agents**
  and in the Application Insights **Agents (preview)** view.
* [Microsoft OpenTelemetry Distro][ms-otel] sends telemetry to Application
  Insights through its Azure Monitor exporter.
* Stable, configuration-driven agent IDs can be changed through App Service
  application settings without changing source code.
* Each agent is registered as an external agent in the Foundry project by an
  `azd` postdeploy hook, so every agent gets its own entry under **Agents** in
  the Foundry portal with its traces and run history attached.
* Azure AI Foundry project with fixed per-agent deployments: GPT-6 Astra for
  coordination, GPT-5.6 Luna for planning, and GPT-5.4 Mini for tool-focused
  specialists.
* Agents use the Azure OpenAI Responses API rather than Chat Completions. This
  is required for GPT-6 Astra when function tools are combined with reasoning
  and also gives the model-router path the same tool-compatible client.
* A Balanced `model-router` deployment can replace the fixed assignments for
  any chat. Its pool is limited to the same three model tiers so routing stays
  easy to explain and observe.
* `azd up` deploys App Service plan, Linux Web App (.NET 9), Application
  Insights (workspace-based), Log Analytics workspace, and the Azure AI
  Foundry account and project in one shot.
* Minimal chat UI with markdown rendering, a model-strategy switch, and a
  "+ New chat" reset button.

## What lights up

After you deploy and exercise the app, two new portal experiences are populated:

* **App Service → AI (preview) → Agents** — per-agent calls, tokens, error
  rate, and a link to view in Application Insights.
* **Application Insights → Agents (preview)** — agent operational metrics,
  tool calls, token consumption by model, and trace drill-downs.
* **Microsoft Foundry → project → Agents** lists every agent in the app. Each
  one is registered as an external agent during deployment, and its **Traces**
  tab shows the runs matched by `gen_ai.agent.id`.

## Getting Started

### Prerequisites

* An Azure subscription with permissions to create App Service, Application
  Insights, and an Azure AI Foundry resource.
* Quota for the model you deploy in your target region. See
  [Azure OpenAI quotas](https://learn.microsoft.com/azure/ai-services/openai/quotas-limits).
* The [.NET 9 SDK](https://dotnet.microsoft.com/download).
* The [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
  version 1.10 or later.

### Quickstart

```bash
git clone https://github.com/Azure-Samples/multi-agent-travel-planner-dotnet.git
cd multi-agent-travel-planner-dotnet
azd auth login
azd up
```

`azd` will prompt for an environment name, subscription, and region. After it
completes, follow the printed URL to chat with the agents. Send a few messages
(for example, *"Plan a 4-day trip to Lisbon in July for two on a midrange
budget"*), then open the portal:

1. Open [Microsoft Foundry](https://ai.azure.com/) and select the project
   created by the deployment.
2. Select **Agents**. All six agents are listed because the postdeploy hook
   registered them. Pick one and open its **Traces** tab. The deployment
   automatically connects the project's Application Insights resource and
   grants the project identity permission to read GenAI trace content.
3. In the App Service resource, select **AI (preview)** → **Agents** and click
   **View in Application Insights** to drill into the Agents (preview) view.

To tear everything down:

```bash
azd down --purge
```

### Run locally

```bash
cd src/MultiAgentTravelPlanner
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://<your-foundry>.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-6-astra"
dotnet user-secrets set "AzureOpenAI:ModelRouterDeployment" "model-router"
dotnet user-secrets set "Agents:Coordinator:Deployment" "gpt-6-astra"
dotnet user-secrets set "Agents:WeatherAdvisor:Deployment" "gpt-5.4-mini"
dotnet user-secrets set "Agents:CurrencyConverter:Deployment" "gpt-5.4-mini"
dotnet user-secrets set "Agents:BudgetOptimizer:Deployment" "gpt-5.6-luna"
dotnet user-secrets set "Agents:LocalKnowledge:Deployment" "gpt-5.4-mini"
dotnet user-secrets set "Agents:ItineraryPlanner:Deployment" "gpt-5.6-luna"
dotnet user-secrets set "ApplicationInsights:ConnectionString" "<your AI connection string>"
dotnet run
```

The app uses [`DefaultAzureCredential`][dac], so signing in with `az login` is
enough and no API keys are required. For local traces to appear in Foundry, use
the connection string of the Application Insights resource connected to that
Foundry project. These deployment names must already exist in the account.
Per-agent settings are optional, and agents without an override use
`AzureOpenAI:Deployment`.

## Demo

The walkthrough that accompanies this sample is the Microsoft Learn tutorial
[Monitor a multi-agent app on App Service with OpenTelemetry and Application Insights (.NET)][tutorial].
It steps through the OpenTelemetry wiring, the portal experience, and common
troubleshooting.

## Project structure

```
.
├── azure.yaml                        # azd config (App Service host, postdeploy hook)
├── infra/                            # Bicep: App Service plan, Web App,
│   ├── main.bicep                    #   App Insights, Azure AI Foundry account + project
│   ├── main.parameters.json
│   └── modules/
├── scripts/
│   ├── register-foundry-agents.sh    # Registers the agents with Foundry (POSIX)
│   └── register-foundry-agents.ps1   # Registers the agents with Foundry (Windows)
├── src/MultiAgentTravelPlanner/
    ├── Program.cs                    # Microsoft OpenTelemetry pipeline
    ├── Agents/AgentCatalog.cs        # Coordinator + 5 specialist agents
    ├── Agents/AgentCatalogRegistry.cs # Fixed and model-router catalogs
    ├── Agents/AgentDeploymentConfiguration.cs # Per-agent model deployment fallback
    ├── Agents/AgentIdConfiguration.cs # Stable configurable agent IDs
    ├── Agents/AgentRoutingMode.cs # Per-chat routing mode parsing
    ├── Tools/TravelTools.cs          # Function tools the agents call
    ├── Models/
    ├── wwwroot/                      # Minimal chat UI
    └── appsettings.json
└── tests/MultiAgentTravelPlanner.Tests/
    ├── AgentDeploymentConfigurationTests.cs # Agent deployment configuration tests
    ├── AgentIdConfigurationTests.cs  # Agent ID configuration tests
    └── AgentRoutingModeTests.cs       # Chat routing mode tests
```

## How OpenTelemetry is wired

`Program.cs` and `Agents/AgentCatalog.cs` together do three things:

1. **Wrap each agent with `UseOpenTelemetry`.** Every agent goes through a
   `WithTelemetry(...)` helper that calls
   `agent.AsBuilder().UseOpenTelemetry(sourceName, o => o.EnableSensitiveData = true).Build()`.
   This is what makes Microsoft Agent Framework emit `gen_ai.agent.name`,
   `gen_ai.agent.id`, `gen_ai.usage.*`, and tool-call spans for every agent
   run.
2. **Configure Microsoft OpenTelemetry** with `UseMicrosoftOpenTelemetry()` and
   enable `ExportTarget.AzureMonitor` to send telemetry to Application Insights.
3. **Subscribe the OpenTelemetry pipeline to the agent activity source** with
   `.AddSource(AgentCatalog.TelemetrySourceName)` and to the OpenAI /
   Extensions.AI sources for the underlying chat-completion spans.

Each agent has a stable logical name and reads its effective ID from
`Agents:<LogicalName>:Id`. The logical name is the default. The `agents` list in
`infra/main.bicep` holds those names and drives two things: one App Service
application setting per agent, and the `AZURE_AI_AGENT_IDS` output that the
registration hook consumes. Keep it in sync with
`AgentCatalog.LogicalAgentNames`.

| Agent | App Service application setting |
|-------|---------------------------------|
| Coordinator | `Agents__Coordinator__Id` |
| WeatherAdvisor | `Agents__WeatherAdvisor__Id` |
| CurrencyConverter | `Agents__CurrencyConverter__Id` |
| BudgetOptimizer | `Agents__BudgetOptimizer__Id` |
| LocalKnowledge | `Agents__LocalKnowledge__Id` |
| ItineraryPlanner | `Agents__ItineraryPlanner__Id` |

IDs must be nonempty, unique, and stable across restarts. The app validates
these requirements at startup.

## Model strategies

The UI offers two strategies. Changing the strategy starts a new chat so one
conversation never mixes session state from different agent catalogs.

### Fixed models

The fixed strategy uses explicit deployment names for predictable behavior and
clear cost attribution.

| Agent | Deployment | Why |
|-------|------------|-----|
| Coordinator | `gpt-6-astra` (`2026-09-03`) | Strong orchestration, tool use, and planning |
| BudgetOptimizer | `gpt-5.6-luna` (`2026-07-09`) | Constraint reasoning and cost tradeoffs |
| ItineraryPlanner | `gpt-5.6-luna` (`2026-07-09`) | Multi-day planning and synthesis |
| WeatherAdvisor | `gpt-5.4-mini` (`2026-03-17`) | Fast tool use and short recommendations |
| CurrencyConverter | `gpt-5.4-mini` (`2026-03-17`) | Fast structured transformation |
| LocalKnowledge | `gpt-5.4-mini` (`2026-03-17`) | Concise, lower-cost responses |

Set `Agents:<LogicalName>:Deployment` to change an assignment. The value is an
Azure deployment name, not necessarily the underlying model name. An agent
without an override uses `AzureOpenAI:Deployment`.

### Model router

The model-router strategy sends every agent request through the `model-router`
deployment. The deployment uses Balanced mode and can select from:

* `gpt-5.4-mini` for simple or latency-sensitive work.
* `gpt-5.6-luna` for planning and synthesis.
* `gpt-6-astra` for difficult orchestration requests.

The response model recorded by OpenTelemetry shows which underlying model
served each request. Azure Monitor can also split model-router metrics by the
underlying model. The router deployment is configured through
`AzureOpenAI:ModelRouterDeployment`.

`infra/main.bicep` contains the fixed assignments, exact model versions, router
pool, and capacity. The defaults were verified against the Australia East
catalog. Check availability and quota before deploying to another region.

The application currently uses `Azure.AI.OpenAI` `2.9.0-beta.1` because the
Responses client is not available in the earlier stable package used by this
sample. The Responses client is marked experimental by the OpenAI SDK, so the
small, localized compiler warning suppression in `AgentCatalog` is intentional.

## How agents are registered with Foundry

Traces alone put the agents in Application Insights, but the Foundry portal only
lists an agent under **Agents** once it has a registration whose `otel_agent_id`
matches the `gen_ai.agent.id` attribute on the spans. `azure.yaml` automates
that with a `postdeploy` hook:

```yaml
hooks:
  postdeploy:
    posix:
      shell: sh
      run: ./scripts/register-foundry-agents.sh
```

The script reads `AZURE_AI_FOUNDRY_PROJECT_ENDPOINT` and `AZURE_AI_AGENT_IDS`
from the `azd` environment, acquires a token with `azd auth token`, and creates
one external agent registration per ID. It skips agents that already exist, so
repeated `azd deploy` runs don't create duplicate registrations or churn agent
versions. Registration happens after deploy rather than after provision, which
means changing agents only requires `azd deploy`.

Two details are worth knowing:

* External agent registration is a Foundry preview API, so the script sends the
  `Foundry-Features: ExternalAgents=V1Preview` header. There's no equivalent
  Bicep resource type or `az` command.
* The identity running `azd` needs the **Foundry User** role on the project.
  `infra/modules/foundry.bicep` grants it to `principalId` during provisioning.

The hook is configured with `continueOnError: true`, so a preview API failure
reports the error without failing the deployment. Run it again on its own with:

```bash
azd hooks run postdeploy
```

## Resources

* [Tutorial: Monitor a multi-agent app on App Service with OpenTelemetry and Application Insights (.NET)][tutorial]
* [Microsoft Agent Framework][maf]
* [Enable Azure Monitor OpenTelemetry for ASP.NET Core][azmon-otel]
* [Microsoft OpenTelemetry Distro][ms-otel]
* [OpenTelemetry generative AI semantic conventions][semconv]
* [Agents (preview) in Application Insights](https://learn.microsoft.com/azure/azure-monitor/app/agents-view)
* [Use model router for Microsoft Foundry](https://learn.microsoft.com/azure/foundry/openai/how-to/model-router)
* [Monitor model router](https://learn.microsoft.com/azure/foundry/openai/how-to/monitor-model-router)
* [Register external agents for observability and evaluation](https://learn.microsoft.com/azure/ai-foundry/agents/how-to/register-external-agent)
* [Customize azd workflows using command and event hooks](https://learn.microsoft.com/azure/developer/azure-developer-cli/azd-extensibility)
* [Build agentic web applications on Azure App Service](https://learn.microsoft.com/azure/app-service/scenario-ai-agentic-web-apps)

## License

MIT — see [LICENSE.md](LICENSE.md).

[maf]: https://learn.microsoft.com/agent-framework/overview/agent-framework-overview
[otel]: https://opentelemetry.io
[semconv]: https://opentelemetry.io/docs/specs/semconv/gen-ai/
[dac]: https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential
[azmon-otel]: https://learn.microsoft.com/azure/azure-monitor/app/opentelemetry-enable
[ms-otel]: https://github.com/microsoft/opentelemetry-distro-dotnet
[tutorial]: https://learn.microsoft.com/azure/app-service/tutorial-ai-agent-monitoring-dotnet
