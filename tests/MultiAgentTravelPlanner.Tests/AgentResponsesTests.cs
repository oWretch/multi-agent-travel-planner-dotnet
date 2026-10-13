using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Tools;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentResponsesTests
{
    [Fact]
    public async Task ResponsesApiUsesStoredResponses()
    {
        var requests = new List<JsonElement>();
        using var httpClient = new HttpClient(new ResponsesHandler(requests));
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
        var catalog = AgentCatalog.BuildWithSingleDeployment(
            CreateClient(httpClient), "router", new TravelTools(), new ConfigurationBuilder().Build());
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
        new Uri("https://demo.openai.azure.com"),
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

    private sealed class ResponsesHandler(List<JsonElement> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
                 "store":true,"model":"test-model","error":null,"output":{{{output}}},
                 "usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ChatCompletionsHandler(List<string> requests) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("demo.openai.azure.com", request.RequestUri!.Host);
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
