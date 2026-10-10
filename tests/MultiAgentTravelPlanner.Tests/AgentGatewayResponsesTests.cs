using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Observability;
using MultiAgentTravelPlanner.Tools;

namespace MultiAgentTravelPlanner.Tests;

[Collection("AI gateway")]
public sealed class AgentGatewayResponsesTests
{
    [Theory]
    [InlineData("AIGateway:UseStatelessResponses")]
    [InlineData("AZURE_AI_GATEWAY_STATELESS_RESPONSES")]
    public async Task StatelessResponsesPreserveToolResultsAndFollowUpHistory(string configurationKey)
    {
        var requests = new List<JsonElement>();
        var urls = new List<Uri>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(nameof(AgentGatewayResponsesTests));
        using var root = source.StartActivity("POST /api/chat");
        root!.SetTag("travel_planner.routing.mode", "fixed");
        using var httpClient = new HttpClient(new AIGatewayTelemetryHandler
        {
            InnerHandler = new ResponsesHandler(requests, urls, stored: false, verifyAgentHeaders: true),
        });
        var client = CreateClient(httpClient);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [configurationKey] = "true" })
            .Build();
        var catalog = AgentCatalog.Build(client, "test-model", new TravelTools(), configuration);
        var agent = catalog.Specialists.Single(a => a.Name == "WeatherAdvisor");
        var session = await agent.CreateSessionAsync();

        var first = await agent.RunAsync("Weather in Lisbon?", session);
        var second = await agent.RunAsync("What should I pack?", session);
        var replay = await agent.RunAsync("Weather in Lisbon?");

        Assert.Equal("Pack a light jacket.", first.Text);
        Assert.Equal("Pack a light jacket.", second.Text);
        Assert.Equal(first.Text, replay.Text);
        Assert.Equal(4, requests.Count);
        Assert.Equal(requests[0].GetRawText(), requests[3].GetRawText());
        Assert.All(urls, url =>
        {
            Assert.Equal("demo.azure-api.net", url.Host);
            Assert.Contains(url.AbsolutePath, new[] { "/openai/responses", "/openai/v1/responses" });
        });
        Assert.All(requests, request =>
        {
            Assert.False(request.GetProperty("store").GetBoolean());
            Assert.False(request.TryGetProperty("previous_response_id", out _));
            Assert.False(request.TryGetProperty("conversation", out _));
            Assert.Contains(request.GetProperty("include").EnumerateArray(),
                item => item.GetString() == "reasoning.encrypted_content");
        });
        var toolLoopInput = requests[1].GetProperty("input").EnumerateArray().ToArray();
        Assert.Contains(toolLoopInput, item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output"
            && item.GetProperty("output").GetString()!.Contains("Forecast for Lisbon", StringComparison.Ordinal));
        Assert.Contains(toolLoopInput, item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "reasoning"
            && item.GetProperty("id").GetString() == "rs_1"
            && item.GetProperty("encrypted_content").GetString() == "encrypted-demo-reasoning");
        var followUpInput = requests[2].GetProperty("input").GetRawText();
        Assert.Contains("Weather in Lisbon?", followUpInput, StringComparison.Ordinal);
        Assert.Contains("What should I pack?", followUpInput, StringComparison.Ordinal);
        Assert.Contains("Pack a light jacket.", followUpInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectBackendRetainsStoredResponsesWhenGatewaySettingIsAbsent()
    {
        var requests = new List<JsonElement>();
        using var httpClient = new HttpClient(new ResponsesHandler(requests, [], stored: true));
        var catalog = AgentCatalog.Build(
            CreateClient(httpClient),
            "test-model",
            new TravelTools(),
            new ConfigurationBuilder().Build());
        var agent = catalog.Specialists.Single(a => a.Name == "WeatherAdvisor");

        await agent.RunAsync("Weather in Lisbon?");

        Assert.False(requests[0].TryGetProperty("store", out _));
        Assert.Equal("resp_1", requests[1].GetProperty("previous_response_id").GetString());
    }

    [Fact]
    public async Task ModelRouterRetainsChatCompletionsAndIdenticalNewChatRequests()
    {
        var requests = new List<string>();
        using var httpClient = new HttpClient(new ChatCompletionsHandler(requests));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AZURE_AI_GATEWAY_STATELESS_RESPONSES"] = "true",
            })
            .Build();
        var catalog = AgentCatalog.BuildWithSingleDeployment(
            CreateClient(httpClient), "router", new TravelTools(), configuration);
        var agent = catalog.Specialists.Single(a => a.Name == "ItineraryPlanner");

        var first = await agent.RunAsync("Plan three days in Lisbon.");
        var second = await agent.RunAsync("Plan three days in Lisbon.");

        Assert.Equal("Visit Lisbon.", first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
        using var body = JsonDocument.Parse(requests[0]);
        Assert.True(body.RootElement.TryGetProperty("messages", out _));
        Assert.False(body.RootElement.TryGetProperty("include", out _));
        Assert.False(body.RootElement.TryGetProperty("store", out _));
    }

    private static AzureOpenAIClient CreateClient(HttpClient httpClient) => new(
        new Uri("https://demo.azure-api.net"),
        new TestCredential(),
        new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(httpClient) });

    private sealed class TestCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ResponsesHandler(
        List<JsonElement> requests,
        List<Uri> urls,
        bool stored,
        bool verifyAgentHeaders = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (verifyAgentHeaders)
            {
                Assert.Equal("WeatherAdvisor", request.Headers.GetValues("x-agent-name").Single());
                Assert.Equal("fixed", request.Headers.GetValues("x-routing-mode").Single());
            }
            urls.Add(request.RequestUri!);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            requests.Add(body.RootElement.Clone());
            var count = requests.Count;
            var output = count == 1
                ? """
                  [
                    {"type":"reasoning","id":"rs_1","summary":[],"encrypted_content":"encrypted-demo-reasoning"},
                    {"type":"function_call","id":"fc_1","call_id":"call_1","name":"GetWeatherForecast","arguments":"{\"destination\":\"Lisbon\",\"date\":\"2026-07-12\"}","status":"completed"}
                  ]
                  """
                : """
                  [
                    {"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Pack a light jacket.","annotations":[]}]}
                  ]
                  """;
            var json = $$$"""
                {"id":"resp_{{{count}}}","object":"response","created_at":1791620000,"status":"completed",
                 "store":{{{stored.ToString().ToLowerInvariant()}}},"model":"test-model","error":null,"output":{{{output}}},
                 "usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            response.Headers.Add("x-ai-cache", "hit");
            return response;
        }
    }

    private sealed class ChatCompletionsHandler(List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("demo.azure-api.net", request.RequestUri!.Host);
            Assert.Equal("/openai/deployments/router/chat/completions", request.RequestUri.AbsolutePath);
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"chatcmpl_1","object":"chat.completion","created":1791620000,"model":"router",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"Visit Lisbon."},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
