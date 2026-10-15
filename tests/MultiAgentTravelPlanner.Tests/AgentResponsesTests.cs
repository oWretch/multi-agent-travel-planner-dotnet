using System.ClientModel.Primitives;
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

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ReasoningIsPlainTextInSpansAndEvents(bool streaming, bool readableReasoning)
    {
        var requests = new List<JsonElement>();
        using var capture = new TelemetryCapture();
        using var httpClient = new HttpClient(new ResponsesHandler(requests, readableReasoning));
        var catalog = AgentCatalog.Build(
            CreateClient(httpClient), "test-model", new TravelTools(), CreateTelemetryConfiguration(true, streaming));

        await catalog.Specialists.Single(a => a.Name == "WeatherAdvisor").RunAsync("Weather in Lisbon?");

        Assert.Equal("concise", requests[0].GetProperty("reasoning").GetProperty("summary").GetString());
        var spans = capture.Activities.Where(a =>
            a.GetTagItem("gen_ai.operation.name") is "chat" or "invoke_agent").ToList();
        Assert.Contains(spans, a => a.Events.Any(e => e.Name == GenAiSpanEventProcessor.InferenceEventName));
        Assert.Contains(spans, a => a.Events.Any(e => e.Name == GenAiSpanEventProcessor.AgentEventName));
        foreach (var span in spans)
        {
            var values = span.TagObjects.Select(t => t.Value as string)
                .Concat(span.Events.SelectMany(e => e.Tags.Select(t => t.Value as string)))
                .Where(v => v is not null).ToList();
            Assert.DoesNotContain(values, v => v!.Contains("encrypted-demo-reasoning") || v.Contains("TextReasoningContent"));
        }

        var eventOutputs = spans.SelectMany(a => a.Events)
            .Select(e => e.Tags.FirstOrDefault(t => t.Key == "gen_ai.output.messages").Value as string)
            .Where(v => v is not null).ToList();
        Assert.Equal(readableReasoning, eventOutputs.Any(v => v!.Contains("\"type\": \"reasoning\"") && v.Contains("Check the forecast.")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledSensitiveCaptureOmitsContentAndSummaryRequest(bool streaming)
    {
        var requests = new List<JsonElement>();
        using var capture = new TelemetryCapture(captureSensitiveData: false);
        using var httpClient = new HttpClient(new ResponsesHandler(requests, readableReasoning: true));
        var catalog = AgentCatalog.Build(
            CreateClient(httpClient), "test-model", new TravelTools(), CreateTelemetryConfiguration(false, streaming));

        await catalog.Specialists.Single(a => a.Name == "WeatherAdvisor").RunAsync("Weather in Lisbon?");

        Assert.False(requests[0].GetProperty("reasoning").TryGetProperty("summary", out _));
        var spans = capture.Activities.Where(a => a.GetTagItem("gen_ai.operation.name") is "chat" or "invoke_agent").ToList();
        Assert.NotEmpty(spans.SelectMany(a => a.Events));
        foreach (var span in spans)
        {
            var tagNames = span.TagObjects.Select(t => t.Key).Concat(span.Events.SelectMany(e => e.Tags.Select(t => t.Key)));
            Assert.DoesNotContain(tagNames, n => n is "gen_ai.input.messages" or "gen_ai.output.messages");
            Assert.DoesNotContain(
                span.Events.SelectMany(e => e.Tags).Select(t => t.Value as string),
                v => v is not null && v.Contains("Check the forecast."));
        }
    }

    private static IConfiguration CreateTelemetryConfiguration(bool captureSensitiveData, bool streaming) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:CaptureSensitiveData"] = captureSensitiveData.ToString(),
            ["Telemetry:UseStreaming"] = streaming.ToString(),
            ["Agents:WeatherAdvisor:ReasoningEffort"] = "medium",
        }).Build();

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

    private sealed class ResponsesHandler(List<JsonElement> requests, bool readableReasoning = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            requests.Add(body.RootElement.Clone());
            var count = requests.Count;
            var summary = readableReasoning
                ? """[{"type":"summary_text","text":"Check the forecast."}]"""
                : "[]";
            var items = count == 1
                ? new[]
                {
                    $$"""{"type":"reasoning","id":"rs_1","summary":{{summary}},"encrypted_content":"encrypted-demo-reasoning"}""",
                    """{"type":"function_call","id":"fc_1","call_id":"call_1","name":"GetWeatherForecast","arguments":"{\"destination\":\"Lisbon\",\"date\":\"2026-07-12\"}","status":"completed"}""",
                }
                : new[]
                {
                    """{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"Pack a light jacket.","annotations":[]}]}""",
                };
            var response = $$$"""
                {"id":"resp_{{{count}}}","object":"response","created_at":1791620000,"status":"completed",
                 "store":true,"model":"test-model","error":null,"output":[{{{string.Join(",", items)}}}],
                 "usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}
                """.ReplaceLineEndings(" ");

            var streaming = body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
            if (!streaming)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, Encoding.UTF8, "application/json"),
                };
            }

            var sse = new StringBuilder();
            var sequence = 0;
            void Emit(string type, string json) =>
                sse.Append($"event: {type}\ndata: {json}\n\n");
            Emit("response.created", $$$"""{"type":"response.created","sequence_number":{{{sequence++}}},"response":{{{response}}}}""");
            for (var i = 0; i < items.Length; i++)
            {
                if (count == 1 && i == 0 && readableReasoning)
                {
                    Emit("response.reasoning_summary_text.delta",
                        $$"""{"type":"response.reasoning_summary_text.delta","sequence_number":{{sequence++}},"item_id":"rs_1","output_index":0,"summary_index":0,"delta":"Check the forecast."}""");
                }

                Emit("response.output_item.done",
                    $$$"""{"type":"response.output_item.done","sequence_number":{{{sequence++}}},"output_index":{{{i}}},"item":{{{items[i]}}}}""");
            }

            Emit("response.completed", $$$"""{"type":"response.completed","sequence_number":{{{sequence++}}},"response":{{{response}}}}""");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream"),
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
