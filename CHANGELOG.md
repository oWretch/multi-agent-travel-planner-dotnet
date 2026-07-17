## multi-agent-travel-planner-dotnet Changelog

## Unreleased

* Replace the Azure Monitor-specific OpenTelemetry distribution with Microsoft
  OpenTelemetry while retaining Azure Monitor export.
* Make every agent ID configurable through a stable
  `Agents__<LogicalName>__Id` App Service application setting.
* Validate that configured agent IDs are nonempty and unique at startup.

<a name="0.1.0"></a>
# 0.1.0 (2026-05-14)

*Features*
* Initial sample: ASP.NET Core 9 multi-agent travel planner using Microsoft
  Agent Framework 1.3.0, Azure OpenAI (`gpt-4o`), and the Azure Monitor
  OpenTelemetry distro.
* `azd` template provisioning App Service plan, Linux Web App, Application
  Insights, Log Analytics, and the Azure OpenAI account.
* Per-agent telemetry via the OpenTelemetry GenAI semantic conventions so
  agents surface in the App Service AI (preview) → Agents tab and the
  Application Insights Agents (preview) view.
* Stable agent IDs (`Id = Name`) so the Agents tab aggregates correctly across
  app restarts and deployments.
