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
* Azure OpenAI (`gpt-4o`) via managed identity — no API keys in the app.
* `azd up` deploys App Service plan, Linux Web App (.NET 9), Application
  Insights (workspace-based), Log Analytics workspace, and the Azure OpenAI
  account in one shot.
* Minimal chat UI with markdown rendering and a "+ New chat" reset button.

## What lights up

After you deploy and exercise the app, two new portal experiences are populated:

* **App Service → AI (preview) → Agents** — per-agent calls, tokens, error
  rate, and a link to view in Application Insights.
* **Application Insights → Agents (preview)** — agent operational metrics,
  tool calls, token consumption by model, and trace drill-downs.

## Getting Started

### Prerequisites

* An Azure subscription with permissions to create App Service, Application
  Insights, and Azure OpenAI resources.
* Quota for the `gpt-4o` model in your target region. See
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

1. Open your Web App resource in the [Azure portal](https://portal.azure.com).
2. Select **AI (preview)** → **Agents**.
3. Click **View in Application Insights** to drill into the Agents (preview) view.

To tear everything down:

```bash
azd down --purge
```

### Run locally

```bash
cd src/MultiAgentTravelPlanner
dotnet user-secrets set "AzureOpenAI:Endpoint"   "https://<your-aoai>.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "gpt-4o"
dotnet user-secrets set "ApplicationInsights:ConnectionString" "<your AI connection string>"
dotnet run
```

The app uses [`DefaultAzureCredential`][dac], so signing in with `az login` is
enough — no API keys.

## Demo

The walkthrough that accompanies this sample is the Microsoft Learn tutorial
[Monitor a multi-agent app on App Service with OpenTelemetry and Application Insights (.NET)][tutorial].
It steps through the OpenTelemetry wiring, the portal experience, and common
troubleshooting.

## Project structure

```
.
├── azure.yaml                        # azd config (App Service host)
├── infra/                            # Bicep: App Service plan, Web App,
│   ├── main.bicep                    #   App Insights, Azure OpenAI
│   ├── main.parameters.json
│   └── modules/
├── src/MultiAgentTravelPlanner/
    ├── Program.cs                    # Microsoft OpenTelemetry pipeline
    ├── Agents/AgentCatalog.cs        # Coordinator + 5 specialist agents
    ├── Agents/AgentIdConfiguration.cs # Stable configurable agent IDs
    ├── Tools/TravelTools.cs          # Function tools the agents call
    ├── Models/
    ├── wwwroot/                      # Minimal chat UI
    └── appsettings.json
└── tests/MultiAgentTravelPlanner.Tests/
    └── AgentIdConfigurationTests.cs  # Agent ID configuration tests
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
`Agents:<LogicalName>:Id`. The logical name is the default, and the Bicep
template creates a corresponding App Service application setting for every
agent:

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

## Resources

* [Tutorial: Monitor a multi-agent app on App Service with OpenTelemetry and Application Insights (.NET)][tutorial]
* [Microsoft Agent Framework][maf]
* [Enable Azure Monitor OpenTelemetry for ASP.NET Core][azmon-otel]
* [Microsoft OpenTelemetry Distro][ms-otel]
* [OpenTelemetry generative AI semantic conventions][semconv]
* [Agents (preview) in Application Insights](https://learn.microsoft.com/azure/azure-monitor/app/agents-view)
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
