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
* Fixed-model agents use the Azure OpenAI Responses API. This is required for
  GPT-6 Astra when function tools are combined with reasoning.
* The model-router catalog uses Chat Completions because the current Foundry
  model-router deployment does not support Responses. Its pool excludes GPT-6
  Astra and uses GPT-5.6 Sol as its high-capability tier.
* A Balanced `model-router` deployment can replace the fixed assignments for
  any chat. Its pool is limited to the same three model tiers so routing stays
  easy to explain and observe.
* `azd up` deploys App Service plan, Linux Web App (.NET 10), Application
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

* An Azure subscription with permissions to create App Service,
  Application Insights, an Azure AI Foundry resource, and role assignments.
* Quota for the model you deploy in your target region. See
  [Azure OpenAI quotas](https://learn.microsoft.com/azure/ai-services/openai/quotas-limits).
* The [.NET 10 SDK](https://dotnet.microsoft.com/download).
* The [Azure Developer CLI (azd)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
  version 1.10 or later.

### Quickstart

```bash
git clone https://github.com/Azure-Samples/multi-agent-travel-planner-dotnet.git
cd multi-agent-travel-planner-dotnet
azd auth login
az login
azd env new travel-demo
azd up
```

`azd` will prompt for a subscription and region. `AZURE_PRINCIPAL_ID` is populated automatically from the signed-in
`azd` identity; use the same identity for local testing.
After deployment completes, follow the printed URL to chat with the agents. Send a few messages
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

For an existing environment, run `azd provision` followed by `azd deploy`.

The postdeploy hook registers the agents with the Foundry Agent Service using
the signed-in `azd` identity. Registration is safe to repeat and doesn't
require redeploying the app:

```bash
sh ./scripts/register-foundry-agents.sh
```

On Windows, run `./scripts/register-foundry-agents.ps1` instead. HTTP 401 or
403 indicates an authentication or permission issue; check the signed-in `azd`
identity and its **Foundry User** role on the project. The Agent Service
returns HTTP 404 `Project not found` for some identities, such as a personal
Microsoft account invited to the tenant as a guest, even when they have the
**Foundry User** role.

To tear everything down:

```bash
azd down --purge
```

### Run locally

Sign in and select the same `azd` environment used for deployment:

```bash
az login
azd env select aiengday
```

In VS Code, run the **.NET Core Launch (web)** debug configuration. Its
pre-launch task exports the selected `azd` environment to the ignored
`.vscode/.env` file, builds the app, and opens the browser when the server is
ready. The launch profile restricts `DefaultAzureCredential` to developer
credentials locally, so the active Azure CLI login is used without probing the
App Service managed identity endpoint.

To run without VS Code, either export the `azd` values into the shell or use
user secrets:

```bash
cd src/MultiAgentTravelPlanner
dotnet user-secrets set "AzureOpenAI:Endpoint" "$(azd env get-value AZURE_OPENAI_ENDPOINT)"
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
enough and no API keys are required. Local runs skip managed identity and use
the developer credential chain; App Service retains managed identity. For local
traces to appear in Foundry, use the connection string of the Application
Insights resource connected to that Foundry project. These deployment names
must already exist in the account. Per-agent settings are optional, and agents
without an override use `AzureOpenAI:Deployment`.

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

`Program.cs` and `Agents/AgentCatalog.cs` configure agent telemetry:

1. **Wrap each agent with `UseOpenTelemetry`.** Every agent goes through a
   `WithTelemetry(...)` helper that calls
   `agent.AsBuilder().UseOpenTelemetry(...)`. The
   `Telemetry:CaptureSensitiveData` setting controls content capture at both
   the agent and model layers.
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

### GenAI telemetry coverage

The app follows the [GenAI attribute registry](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/registry/attributes/gen-ai.md)
and its [inference conventions](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/client-inference.md).
These conventions are still in development. Microsoft.Extensions.AI provides
the base instrumentation; `GenAiTelemetryChatClient` enriches its existing
model spans without creating another span for the same request.

| Surface | Captured attributes |
|---------|---------------------|
| Agent identity | `gen_ai.agent.id`, `.name`, `.description` on agent runs and their model/tool spans |
| Model request | `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.request.model`, `server.address`, `server.port`; sampling/token-limit settings when supplied |
| Reasoning | `gen_ai.request.reasoning.level` when explicitly requested; `gen_ai.usage.reasoning.output_tokens` when the provider reports it |
| Streaming | `gen_ai.request.stream`; `gen_ai.response.time_to_first_chunk` in seconds, from model request issuance to its first received chunk |
| Model response | `gen_ai.response.id`, `.model`, `.finish_reasons`; `.status` for Responses API lifecycle events |
| Usage | `gen_ai.usage.input_tokens`, `.output_tokens`, `.cache_read.input_tokens`; text/audio breakdowns when supplied by the SDK |
| Context | `gen_ai.conversation.id` from the app session or a hosted provider conversation; `gen_ai.request.previous_response.id` for Responses continuations |
| Tools | `gen_ai.tool.definitions` as JSON with function names, descriptions, and parameter schemas; `gen_ai.tool.name`, `.description`, `.type`, `.call.id` on execution spans |
| Content (opt-in) | `gen_ai.system_instructions`, `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` |
| Span events | One completion event per model call, agent run, and tool execution, with correlated metadata and opt-in content |
| Errors | Error span status and `error.type`; failed tool executions omit results, and failed model generations without a finish reason report `error` |

Reasoning and cached tokens are subsets of the provider's output and input
totals respectively. Do not add them to those totals. Use model spans
(`gen_ai.operation.name == "chat"`) for cost analysis: agent-run spans also
contain token rollups, so summing both layers double-counts usage.

`Telemetry:UseStreaming` defaults to `true` in the demo's `appsettings.json`.
Every model request uses streaming, including specialist requests made through
agent-as-tool calls. `StreamingChatClient` buffers those updates, so the
`/api/chat` JSON response and UI stay unchanged. The SDK also emits
`gen_ai.client.operation.time_to_first_chunk` and `gen_ai.client.operation.time_per_output_chunk`
histograms alongside operation duration and token usage metrics. Setting
`Telemetry__UseStreaming=false` restores buffered provider requests; those
requests correctly omit first-chunk timing.

Fixed-model agents read `Agents:<LogicalName>:ReasoningEffort`. The demo uses
`low` for the tool-focused specialists and `medium` for the coordinator and
planners. Supported values are `none`, `low`, `medium`, `high`, and `xhigh`;
the telemetry value matches the string sent to OpenAI. Remove the setting to
use the provider default, which leaves `gen_ai.request.reasoning.level` unset.
Only configure effort levels your deployed model supports. The model-router
catalog leaves effort to the provider rather than applying fixed-model settings,
but still captures reasoning token usage when returned.

The demo explicitly enables `Telemetry:CaptureSensitiveData` in
`appsettings.json`. It includes user messages, instructions, and tool
arguments/results in Application Insights. Use synthetic travel requests for
the demo. Set `Telemetry__CaptureSensitiveData=false` to omit this content at
both telemetry layers. Restrict access and retention on the telemetry backend.
Tool definitions remain available as model-interface metadata.

### Content span events

`GenAiSpanEventProcessor` attaches a completion event to each recorded model,
agent, and tool span. It runs after conversation and agent enrichment, so each
event carries the same attribution as its span. Existing content attributes
remain available for portal views.

| Operation | Span event |
|-----------|------------|
| Model call (`chat`) | `gen_ai.client.inference.operation.details` |
| Agent run (`invoke_agent`) | `travel_planner.agent.operation.details` |
| Tool execution (`execute_tool`) | `travel_planner.tool.operation.details` |

Events include the operation, agent/conversation IDs, model or tool identity,
and response/error metadata when available. With
`Telemetry:CaptureSensitiveData=true`, they also include the SDK's JSON content
attributes: system instructions (prompts), input/output messages, tool
definitions, and tool arguments/results. Reasoning text or summaries returned
by the provider appear as `{"type":"reasoning","content":"..."}` parts in the
message JSON, including streamed reasoning. Encrypted reasoning state is not
recorded. Reasoning token counts do not imply that reasoning text is available;
the app neither requests summaries automatically nor reconstructs hidden
reasoning.

Setting `Telemetry__CaptureSensitiveData=false` leaves only metadata on these
events. Failed tool events omit results. Failed model events retain only
content that the SDK actually captured, without fabricated output.

The [GenAI event conventions](https://github.com/open-telemetry/semantic-conventions-genai/blob/main/docs/gen-ai/client-inference.md#events)
define `gen_ai.client.inference.operation.details` as a **log event**, with
structured content. This sample maps that name and its content attributes to
.NET `ActivityEvent` span events, using JSON strings because the Azure Monitor
trace exporter stores event attributes as strings. This is a span-event
adaptation, not a structured GenAI log-event implementation. The agent/tool
event names are sample-specific.

Azure Monitor exports these span events to Application Insights **`traces`**,
not `customEvents`. `operation_Id` identifies the trace and
`operation_ParentId` identifies the owning span. Trace sampling and exporter
size limits apply; copying content onto events also increases telemetry volume.
Inspect them with:

```kusto
traces
| where message in (
    "gen_ai.client.inference.operation.details",
    "travel_planner.agent.operation.details",
    "travel_planner.tool.operation.details")
| project timestamp, message, operation_Id, operation_ParentId,
    agent = tostring(customDimensions["gen_ai.agent.name"]),
    conversation = tostring(customDimensions["gen_ai.conversation.id"]),
    instructions = parse_json(tostring(customDimensions["gen_ai.system_instructions"])),
    inputs = parse_json(tostring(customDimensions["gen_ai.input.messages"])),
    outputs = parse_json(tostring(customDimensions["gen_ai.output.messages"])),
    toolArguments = parse_json(tostring(customDimensions["gen_ai.tool.call.arguments"])),
    toolResult = parse_json(tostring(customDimensions["gen_ai.tool.call.result"]))
| order by timestamp desc
```

### Telemetry limits and queries

The app does not synthesize unavailable usage or provider defaults. It also
does not emit embedding, retrieval, memory, evaluation, skill, or context
compaction attributes because it does not perform those operations. Failed or
cancelled Responses lifecycle statuses propagate exceptions rather than
returning empty successful answers.

In Application Insights Logs, inspect model calls with:

```kusto
dependencies
| where tostring(customDimensions["gen_ai.operation.name"]) == "chat"
| project timestamp,
    agent = tostring(customDimensions["gen_ai.agent.name"]),
    conversation = tostring(customDimensions["gen_ai.conversation.id"]),
    requestedModel = tostring(customDimensions["gen_ai.request.model"]),
    actualModel = tostring(customDimensions["gen_ai.response.model"]),
    reasoningLevel = tostring(customDimensions["gen_ai.request.reasoning.level"]),
    reasoningTokens = tolong(customDimensions["gen_ai.usage.reasoning.output_tokens"]),
    cachedTokens = tolong(customDimensions["gen_ai.usage.cache_read.input_tokens"]),
    firstChunkSeconds = todouble(customDimensions["gen_ai.response.time_to_first_chunk"]),
    duration, success, operation_Id
| order by timestamp desc
```

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
* `gpt-5.6-sol` for difficult orchestration and complex tool use.

The router intentionally excludes `gpt-6-astra`: model-router currently uses
Chat Completions, while GPT-6 Astra requires Responses when reasoning and
function tools are combined.

The response model recorded by OpenTelemetry shows which underlying model
served each request. Azure Monitor can also split model-router metrics by the
underlying model. The router deployment is configured through
`AzureOpenAI:ModelRouterDeployment`.

`infra/main.bicep` contains the fixed assignments, exact model versions, router
pool, and capacity. The defaults were verified against the Australia East
catalog. Check availability and quota before deploying to another region.

The fixed-model catalog currently uses `Azure.AI.OpenAI` `2.9.0-beta.1`
because the Responses client is not available in the earlier stable package
used by this sample. The Responses client is marked experimental by the OpenAI
SDK, so the small, localized compiler warning suppression in `AgentCatalog` is
intentional.

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

### Reasoning in telemetry

Reasoning is recorded only as readable text, as `{"type":"reasoning","content":"..."}` parts in `gen_ai.input.messages` and `gen_ai.output.messages`, and in the matching span events. Providers can also return encrypted reasoning (`protectedData`). It can't be decoded, so the processor removes it from telemetry. When `Telemetry:CaptureSensitiveData` is true and an agent sets `ReasoningEffort`, the Responses request asks for a reasoning summary so there is text to record. If the deployment returns no summary, no reasoning part appears.
