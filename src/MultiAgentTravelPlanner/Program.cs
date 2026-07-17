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
// Azure OpenAI client (managed identity, no keys)
// ---------------------------------------------------------------------------

var aoaiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"]
    ?? throw new InvalidOperationException("AzureOpenAI:Endpoint is required.");
var aoaiDeployment = builder.Configuration["AzureOpenAI:Deployment"]
    ?? throw new InvalidOperationException("AzureOpenAI:Deployment is required.");

builder.Services.AddSingleton(new AzureOpenAIClient(
    new Uri(aoaiEndpoint),
    new DefaultAzureCredential()));

// ---------------------------------------------------------------------------
// Agents
//
// Each AIAgent is a top-level entity that the Agents view groups telemetry by
// through gen_ai.agent.name and gen_ai.agent.id.
// ---------------------------------------------------------------------------

builder.Services.AddSingleton<TravelTools>();
builder.Services.AddSingleton<AgentCatalog>(sp =>
{
    var client = sp.GetRequiredService<AzureOpenAIClient>();
    var tools = sp.GetRequiredService<TravelTools>();
    return AgentCatalog.Build(client, aoaiDeployment, tools, builder.Configuration);
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
    AgentCatalog catalog,
    ConversationStore store,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Message))
    {
        return Results.BadRequest(new { error = "Message is required." });
    }

    var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
        ? Guid.NewGuid().ToString("N")
        : req.SessionId!;
    var session = await store.GetOrCreateAsync(sessionId, catalog.Coordinator, ct);

    var response = await catalog.Coordinator.RunAsync(req.Message, session, cancellationToken: ct);

    return Results.Ok(new ChatResponse(sessionId, response.Text ?? string.Empty));
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
