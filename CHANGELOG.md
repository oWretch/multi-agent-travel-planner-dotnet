## multi-agent-travel-planner-dotnet Changelog

## Unreleased

* Replace the Azure Monitor-specific OpenTelemetry distribution with Microsoft
  OpenTelemetry while retaining Azure Monitor export.
* Make every agent ID configurable through a stable
  `Agents__<LogicalName>__Id` App Service application setting.
* Validate that configured agent IDs are nonempty and unique at startup.
* Deploy a Microsoft Foundry account and project instead of a standalone Azure
  OpenAI account.
* Connect Application Insights to the Foundry account and project so agent
  traces are available in the Foundry tracing view.
* Register every agent as an external agent in the Foundry project from an
  `azd` postdeploy hook so each one appears under Agents in the portal with its
  own traces. The hook is idempotent and reruns with `azd deploy`.
* Drive the per-agent App Service application settings and the registration
  hook from a single `agents` list in `infra/main.bicep`.
* Grant the deploying identity the Foundry User role on the project so the
  registration hook works without manual role setup.
* Deploy GPT-6 Astra, GPT-5.6 Luna, and GPT-5.4 Mini and assign them to
  agents according to each role's reasoning and latency needs.
* Add a Balanced model-router deployment and a per-chat UI switch between
  fixed assignments and adaptive model routing.

<a name="0.1.0"></a>
# 0.1.0 (2026-05-14)

*Features*
* Initial sample: ASP.NET Core 9 multi-agent travel planner using Microsoft
  Agent Framework 1.3.0, Azure OpenAI (`gpt-5.6-terra`), and the Azure Monitor
  OpenTelemetry distro.
* `azd` template provisioning App Service plan, Linux Web App, Application
  Insights, Log Analytics, and the Azure OpenAI account.
* Per-agent telemetry via the OpenTelemetry GenAI semantic conventions so
  agents surface in the App Service AI (preview) → Agents tab and the
  Application Insights Agents (preview) view.
* Stable agent IDs (`Id = Name`) so the Agents tab aggregates correctly across
  app restarts and deployments.
