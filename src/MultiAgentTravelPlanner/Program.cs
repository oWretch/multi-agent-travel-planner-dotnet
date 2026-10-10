using System.Diagnostics;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.OpenTelemetry;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Models;
using MultiAgentTravelPlanner.Tools;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// OpenTelemetry
//
// Microsoft OpenTelemetry configures the instrumentation and Azure Monitor
// exporter. The custom sources below capture the Microsoft Agent Framework and
// underlying OpenAI spans that carry the gen_ai.* semantic conventions.
// ---------------------------------------------------------------------------

var appInsightsConnectionString =
    builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    appInsightsConnectionString =
        builder.Configuration["ApplicationInsights:ConnectionString"];
}

if (string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    throw new InvalidOperationException(
        "APPLICATIONINSIGHTS_CONNECTION_STRING is required.");
}

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .ConfigureResource(r => r
            .AddService(serviceName: "multi-agent-travel-planner"))
        .AddSource(AgentCatalog.TelemetrySourceName)
        .AddSource("Microsoft.Extensions.AI*")
        .AddSource("OpenAI*")
        .AddSource("Experimental.OpenAI*")
        .AddSource("Azure.AI.OpenAI*"))
    .WithMetrics(m => m
        .ConfigureResource(r => r
            .AddService(serviceName: "multi-agent-travel-planner"))
        .AddMeter(AgentCatalog.TelemetrySourceName)
        .AddMeter("Microsoft.Extensions.AI*")
        .AddMeter("OpenAI*"));

builder.UseMicrosoftOpenTelemetry(options =>
{
    options.Exporters = ExportTarget.AzureMonitor;
    options.AzureMonitor.ConnectionString = appInsightsConnectionString;
});

// ---------------------------------------------------------------------------
// Azure OpenAI client for the model deployed in the Foundry account
// (managed identity, no keys)
// ---------------------------------------------------------------------------

var aoaiEndpoint = GetRequiredSetting(
    builder.Configuration,
    "AzureOpenAI:Endpoint",
    "AZURE_OPENAI_ENDPOINT");
var aoaiDeployment = GetRequiredSetting(
    builder.Configuration,
    "AzureOpenAI:Deployment",
    "AZURE_OPENAI_DEPLOYMENT");
var modelRouterDeployment = GetRequiredSetting(
    builder.Configuration,
    "AzureOpenAI:ModelRouterDeployment",
    "AZURE_OPENAI_MODEL_ROUTER_DEPLOYMENT");

var isAzureHosted =
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME"))
    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT"));

builder.Services.AddSingleton(new AzureOpenAIClient(
    new Uri(aoaiEndpoint),
    new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        // App Service uses managed identity. Local development skips the
        // unavailable IMDS endpoint and continues through developer credentials,
        // including the active Azure CLI login.
        ExcludeManagedIdentityCredential = !isAzureHosted,
    })));

// ---------------------------------------------------------------------------
// Agents
//
// Each AIAgent is a top-level entity that the Agents view groups telemetry by
// through gen_ai.agent.name and gen_ai.agent.id.
// ---------------------------------------------------------------------------

builder.Services.AddSingleton<TravelTools>();
builder.Services.AddSingleton<AgentCatalogRegistry>(sp =>
{
    var client = sp.GetRequiredService<AzureOpenAIClient>();
    var tools = sp.GetRequiredService<TravelTools>();
    var fixedCatalog = AgentCatalog.Build(
        client,
        aoaiDeployment,
        tools,
        builder.Configuration);
    var modelRouterCatalog = AgentCatalog.BuildWithSingleDeployment(
        client,
        modelRouterDeployment,
        tools,
        builder.Configuration);

    return new AgentCatalogRegistry(fixedCatalog, modelRouterCatalog);
});

// In-memory per-session conversation state. Keep it simple — this sample is
// about monitoring, not state management.
builder.Services.AddSingleton<ConversationStore>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.MapPost("/api/chat", async (
    ChatRequest req,
    AgentCatalogRegistry catalogs,
    ConversationStore store,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    if (!AgentRoutingModeParser.TryParse(req.RoutingMode, out var routingMode))
    {
        return Results.BadRequest(new
        {
            error = "RoutingMode must be either 'fixed' or 'model-router'.",
        });
    }

    var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
        ? Guid.NewGuid().ToString("N")
        : req.SessionId!;
    var coordinator = catalogs.ResolveCoordinator(routingMode);
    var session = await store.GetOrCreateAsync(
        sessionId,
        routingMode,
        coordinator,
        ct);

    Activity.Current?.SetTag(
        "travel_planner.routing.mode",
        routingMode.ToConfigurationValue());

    var response = await coordinator.RunAsync(
        req.Message,
        session,
        cancellationToken: ct);

    return Results.Ok(new ChatResponse(
        sessionId,
        response.Text ?? string.Empty,
        routingMode.ToConfigurationValue()));
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

static string GetRequiredSetting(
    IConfiguration configuration,
    string configurationKey,
    string environmentVariable)
{
    var value = configuration[configurationKey];
    if (string.IsNullOrWhiteSpace(value))
    {
        value = Environment.GetEnvironmentVariable(environmentVariable);
    }

    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException(
            $"{configurationKey} or {environmentVariable} is required.")
        : value;
}
