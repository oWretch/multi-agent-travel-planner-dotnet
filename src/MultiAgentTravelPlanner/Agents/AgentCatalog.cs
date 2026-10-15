using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MultiAgentTravelPlanner.Observability;
using MultiAgentTravelPlanner.Tools;
using OpenAI.Responses;

namespace MultiAgentTravelPlanner.Agents;

/// <summary>
/// Builds the coordinator and the specialized agents.
///
/// Telemetry is applied at two layers. Each AIAgent is wrapped with
/// <c>UseOpenTelemetry</c> so its runs emit <c>invoke_agent</c> spans carrying
/// <c>gen_ai.agent.name</c>, <c>gen_ai.agent.id</c>, and a per-run
/// <c>gen_ai.usage.*</c> rollup, which is what the App Service Agents tab and
/// the Application Insights Agents (preview) view group on. The IChatClient
/// underneath each agent is wrapped as well, so every individual model call
/// emits its own <c>chat</c> span.
/// </summary>
public sealed class AgentCatalog
{
    public const string TelemetrySourceName = "MultiAgentTravelPlanner.Agents";

    public static IReadOnlyList<string> LogicalAgentNames { get; } =
    [
        "Coordinator",
        "WeatherAdvisor",
        "CurrencyConverter",
        "BudgetOptimizer",
        "LocalKnowledge",
        "ItineraryPlanner",
    ];

    public required AIAgent Coordinator { get; init; }
    public required IReadOnlyList<AIAgent> Specialists { get; init; }

    public static AgentCatalog Build(
        AzureOpenAIClient client,
        string defaultDeployment,
        TravelTools tools,
        IConfiguration configuration) =>
        BuildCore(
            client,
            tools,
            configuration,
            logicalName => AgentDeploymentConfiguration.Resolve(
                configuration,
                logicalName,
                defaultDeployment),
            useResponsesApi: true);

    public static AgentCatalog BuildWithSingleDeployment(
        AzureOpenAIClient client,
        string deployment,
        TravelTools tools,
        IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        return BuildCore(
            client,
            tools,
            configuration,
            _ => deployment,
            useResponsesApi: false);
    }

    private static AgentCatalog BuildCore(
        AzureOpenAIClient client,
        TravelTools tools,
        IConfiguration configuration,
        Func<string, string> resolveDeployment,
        bool useResponsesApi)
    {
        AgentIdConfiguration.ValidateDistinct(configuration, LogicalAgentNames);

        AIAgent weather = WithTelemetry(CreateAgent(client,
            resolveDeployment("WeatherAdvisor"),
            configuration,
            logicalName: "WeatherAdvisor",
            description: "Provides weather forecasts, packing recommendations, and activity suggestions based on destination weather conditions.",
            instructions: "You are a concise weather and packing advisor. Use the weather tool to look up the forecast before answering. Recommend appropriate clothing and one or two activities suited to the conditions.",
            tools: [AIFunctionFactory.Create(tools.GetWeatherForecast)],
            useResponsesApi), configuration);

        AIAgent currency = WithTelemetry(CreateAgent(client,
            resolveDeployment("CurrencyConverter"),
            configuration,
            logicalName: "CurrencyConverter",
            description: "Handles currency conversion, exchange rate information, and budget allocation across different currencies for travelers.",
            instructions: "You are a currency conversion specialist. Use the exchange-rate tool and present results in a short summary table.",
            tools: [AIFunctionFactory.Create(tools.GetExchangeRate)],
            useResponsesApi), configuration);

        AIAgent budget = WithTelemetry(CreateAgent(client,
            resolveDeployment("BudgetOptimizer"),
            configuration,
            logicalName: "BudgetOptimizer",
            description: "Optimizes travel budget allocation across accommodation, food, activities, and transport with cost-saving strategies.",
            instructions: "You optimize a travel budget across lodging, food, activities, and transport. Use the daily-budget tool to ground your suggestions and propose at most three cost-saving tips.",
            tools: [AIFunctionFactory.Create(tools.GetDailyBudget)],
            useResponsesApi), configuration);

        AIAgent local = WithTelemetry(CreateAgent(client,
            resolveDeployment("LocalKnowledge"),
            configuration,
            logicalName: "LocalKnowledge",
            description: "Provides destination-specific knowledge including cultural insights, safety tips, local transportation, and authentic experiences.",
            instructions: "You are a local expert and cultural guide. Use the cultural-tip tool when relevant. Keep responses under 120 words.",
            tools: [AIFunctionFactory.Create(tools.GetCulturalTip)],
            useResponsesApi), configuration);

        AIAgent itinerary = WithTelemetry(CreateAgent(client,
            resolveDeployment("ItineraryPlanner"),
            configuration,
            logicalName: "ItineraryPlanner",
            description: "Creates detailed day-by-day travel itineraries with timing, venues, meal recommendations, and weather considerations.",
            instructions: "You build a clear day-by-day itinerary. Be specific about venues and timing. Keep it to 3-5 days unless the user asks for more.",
            useResponsesApi: useResponsesApi), configuration);

        var specialists = new[] { weather, currency, budget, local, itinerary };

        AIAgent coordinator = WithTelemetry(CreateAgent(client,
            resolveDeployment("Coordinator"),
            configuration,
            logicalName: "Coordinator",
            description: "Coordinates the multi-agent travel planning workflow and aggregates results from specialized agents into a complete travel plan.",
            instructions:
                "You are the travel planning coordinator. Decompose the user's request and call the appropriate specialist agents " +
                "(WeatherAdvisor, CurrencyConverter, BudgetOptimizer, LocalKnowledge, ItineraryPlanner) in parallel where possible. " +
                "Aggregate their answers into a single, well-structured travel plan. Always cite which specialist contributed which section.",
            tools: specialists.Select(s => (AITool)s.AsAIFunction()).ToArray(),
            useResponsesApi), configuration);

        return new AgentCatalog { Coordinator = coordinator, Specialists = specialists };
    }

    private static AIAgent CreateAgent(
        AzureOpenAIClient client,
        string deployment,
        IConfiguration configuration,
        string logicalName,
        string description,
        string instructions,
        IList<AITool>? tools = null,
        bool useResponsesApi = true)
    {
        var options = new ChatClientAgentOptions
        {
            Id = AgentIdConfiguration.Resolve(configuration, logicalName),
            Name = logicalName,
            Description = description,
            ChatOptions = new ChatOptions
            {
                Instructions = instructions,
                Tools = tools,
                Reasoning = useResponsesApi ? GetReasoningOptions(configuration, logicalName) : null,
            },
        };

        IChatClient chatClient;
        if (useResponsesApi)
        {
#pragma warning disable OPENAI001
            chatClient = client.GetResponsesClient().AsIChatClient(deployment);
#pragma warning restore OPENAI001
        }
        else
        {
            chatClient = client.GetChatClient(deployment).AsIChatClient();
        }

        return WithTelemetry(chatClient, configuration, useResponsesApi).AsAIAgent(options);
    }

    /// <summary>
    /// Instruments the inference layer so each model call emits its own
    /// <c>chat</c> span carrying <c>gen_ai.usage.*</c> for that single call.
    /// Without this the agent span reports only a per-run token rollup.
    /// </summary>
    private static IChatClient WithTelemetry(
        IChatClient chatClient,
        IConfiguration configuration,
        bool useResponsesApi)
    {
        var instrumented = chatClient.AsBuilder()
            .UseOpenTelemetry(
                sourceName: TelemetrySourceName,
                configure: otel => otel.EnableSensitiveData =
                    configuration.GetValue<bool>("Telemetry:CaptureSensitiveData"))
            .Use(inner => new GenAiTelemetryChatClient(
                inner,
                useResponsesApi))
            .Build();

        return configuration.GetValue<bool>("Telemetry:UseStreaming")
            ? new StreamingChatClient(instrumented)
            : instrumented;
    }

    private static AIAgent WithTelemetry(AIAgent agent, IConfiguration configuration) =>
        agent.AsBuilder()
            .UseOpenTelemetry(TelemetrySourceName, otel => otel.EnableSensitiveData =
                configuration.GetValue<bool>("Telemetry:CaptureSensitiveData"))
            .Build();

    private static ReasoningOptions? GetReasoningOptions(IConfiguration configuration, string logicalName)
    {
        var key = $"Agents:{logicalName}:ReasoningEffort";
        var value = configuration[key];
        if (value is null)
        {
            return null;
        }

        return new ReasoningOptions
        {
            // Encrypted reasoning is unreadable; a summary gives telemetry plain text to record.
            Output = configuration.GetValue<bool>("Telemetry:CaptureSensitiveData") && value != "none"
                ? ReasoningOutput.Summary
                : null,
            Effort = value switch
            {
                "none" => ReasoningEffort.None,
                "low" => ReasoningEffort.Low,
                "medium" => ReasoningEffort.Medium,
                "high" => ReasoningEffort.High,
                "xhigh" => ReasoningEffort.ExtraHigh,
                _ => throw new InvalidOperationException(
                    $"{key} must be 'none', 'low', 'medium', 'high', or 'xhigh'."),
            },
        };
    }
}
