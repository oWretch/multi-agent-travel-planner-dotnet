using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Observability;
using MultiAgentTravelPlanner.Tools;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentResponsesTests
{
    [Theory]
    [InlineData("fail-once", true)]
    [InlineData("persistent-failure", true)]
    [InlineData("fail-once", false)]
    [InlineData("persistent-failure", false)]
    public async Task WeatherRecoveryPreservesEvidenceAndToolCorrelation(string mode, bool streaming)
    {
        var requests = new List<JsonElement>();
        using var capture = new TelemetryCapture();
        using var httpClient = new HttpClient(new ResponsesHandler(requests, retryTool: true, unavailable: mode == "persistent-failure"));
        var configuration = CreateWeatherDemoConfiguration(mode, streaming);
        var catalog = AgentCatalog.Build(CreateClient(httpClient), "test-model", new TravelTools(configuration), configuration);
        var response = await catalog.Specialists.Single(agent => agent.Name == "WeatherAdvisor").RunAsync("Weather in Lisbon?");

        Assert.Equal(3, requests.Count);
        WeatherLookupResult Outcome(int requestIndex)
        {
            var result = requests[requestIndex].GetProperty("input").EnumerateArray()
                .Single(item => item.GetProperty("type").GetString() == "function_call_output");
            return JsonSerializer.Deserialize<WeatherLookupResult>(result.GetProperty("output").GetString()!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        var failed = Outcome(1);
        var final = Outcome(2);
        Assert.Equal("failed", failed.State);
        Assert.True(failed.Retryable);
        Assert.Equal(mode == "fail-once" ? "succeeded" : "failed", final.State);
        Assert.False(final.Retryable);
        Assert.Equal(failed.LookupId, final.LookupId);
        Assert.Equal(mode == "fail-once" ? "Pack a light jacket." : "Forecast unavailable; advice is general.", response.Text);

        var lookups = capture.Activities.Where(activity => activity.OperationName == "weather_lookup").ToArray();
        Assert.Equal(2, lookups.Length);
        Assert.All(lookups, activity =>
        {
            Assert.Equal("WeatherAdvisor", activity.GetTagItem("gen_ai.agent.name"));
            Assert.Equal("demo-session", activity.GetTagItem("gen_ai.conversation.id"));
            Assert.NotNull(activity.GetTagItem("gen_ai.tool.call.id"));
            Assert.Contains(capture.Activities, parent => parent.SpanId == activity.ParentSpanId
                && parent.GetTagItem("gen_ai.operation.name") is "execute_tool");
        });
        Assert.Equal(lookups[0].GetTagItem("gen_ai.agent.version"), lookups[1].GetTagItem("gen_ai.agent.version"));
    }

    [Fact]
    public async Task RepeatedToolRequestsCannotBypassHostLimits()
    {
        var requests = new List<JsonElement>();
        using var capture = new TelemetryCapture(false);
        using var httpClient = new HttpClient(new ResponsesHandler(requests, endlessTools: true));
        var configuration = CreateWeatherDemoConfiguration("persistent-failure", false);
        var catalog = AgentCatalog.Build(CreateClient(httpClient), "test-model", new TravelTools(configuration), configuration);
        await Record.ExceptionAsync(() => catalog.Specialists.Single(agent => agent.Name == "WeatherAdvisor").RunAsync("Keep retrying."));

        Assert.InRange(requests.Count, 3, 7);
        Assert.Equal(2, capture.Activities.Count(activity => activity.OperationName == "weather_lookup"));
        Assert.DoesNotContain(capture.Activities, activity => activity.GetTagItem("travel_planner.lookup.state") is "succeeded");
    }

    [Theory]
    [InlineData("fail-once")]
    [InlineData("persistent-failure")]
    public async Task ModelRouterWeatherRecoveryUsesStructuredToolOutcomes(string mode)
    {
        var requests = new List<string>();
        using var capture = new TelemetryCapture(false);
        using var httpClient = new HttpClient(new ChatCompletionsHandler(requests, weatherRecovery: true));
        var configuration = CreateWeatherDemoConfiguration(mode, false);
        var catalog = AgentCatalog.BuildWithSingleDeployment(CreateClient(httpClient), "router", new TravelTools(configuration), configuration);
        await catalog.Specialists.Single(agent => agent.Name == "WeatherAdvisor").RunAsync("Weather in Lisbon?");

        Assert.Equal(3, requests.Count);
        using var request = JsonDocument.Parse(requests[2]);
        var results = request.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "tool")
            .Select(message => JsonSerializer.Deserialize<WeatherLookupResult>(message.GetProperty("content").GetString()!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("failed", results[0].State);
        Assert.True(results[0].Retryable);
        Assert.Equal(mode == "fail-once" ? "succeeded" : "failed", results[1].State);
        Assert.False(results[1].Retryable);
        Assert.Equal(2, capture.Activities.Count(activity => activity.OperationName == "weather_lookup"));
    }

    [Fact]
    public async Task ProviderStatusEventsPreserveObservedTransitionsWithoutDuplicates()
    {
        var requests = new List<JsonElement>();
        using var capture = new TelemetryCapture(false);
        using var httpClient = new HttpClient(new ResponsesHandler(requests, responseLifecycle: true));
        var catalog = AgentCatalog.Build(CreateClient(httpClient), "test-model", new TravelTools(), CreateTelemetryConfiguration(false, true));
        await catalog.Specialists.Single(agent => agent.Name == "WeatherAdvisor").RunAsync("Weather in Lisbon?");

        var modelSpans = capture.Activities.Where(activity => activity.GetTagItem("gen_ai.operation.name") is "chat");
        Assert.All(modelSpans, activity =>
        {
            var transitions = activity.Events.Where(spanEvent => spanEvent.Name == GenAiTelemetryChatClient.ResponseStateEventName).ToArray();
            Assert.Equal(new[] { "queued", "in_progress", "completed" }, transitions.Select(spanEvent =>
                spanEvent.Tags.Single(tag => tag.Key == "gen_ai.response.status").Value));
            Assert.Equal(new[] { "none", "queued", "in_progress" }, transitions.Select(spanEvent =>
                spanEvent.Tags.Single(tag => tag.Key == "travel_planner.model.response.previous_state").Value));
            Assert.Equal("completed", activity.GetTagItem("gen_ai.response.status"));
            Assert.All(transitions, spanEvent => Assert.DoesNotContain(spanEvent.Tags, tag => tag.Key == "gen_ai.output.messages"));
        });
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    [InlineData("cancelled")]
    public async Task ProviderTerminalStatesAreNotReportedAsCompleted(string terminal)
    {
        var requests = new List<JsonElement>();
        using var capture = new TelemetryCapture(false);
        using var httpClient = new HttpClient(new ResponsesHandler(requests, terminal: terminal));
        var catalog = AgentCatalog.Build(CreateClient(httpClient), "test-model", new TravelTools(), CreateTelemetryConfiguration(false, false));
        var exception = await Record.ExceptionAsync(() => catalog.Specialists.Single(agent => agent.Name == "WeatherAdvisor").RunAsync("Weather?"));

        if (terminal != "incomplete")
        {
            Assert.NotNull(exception);
        }
        var model = Assert.Single(capture.Activities, activity => activity.GetTagItem("gen_ai.operation.name") is "chat");
        Assert.Equal(terminal, model.GetTagItem("gen_ai.response.status"));
        Assert.DoesNotContain(model.Events, spanEvent => spanEvent.Name == GenAiTelemetryChatClient.ResponseStateEventName
            && spanEvent.Tags.Any(tag => tag.Key == "gen_ai.response.status" && tag.Value is "completed"));
    }

    private static IConfiguration CreateWeatherDemoConfiguration(string mode, bool streaming) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:WeatherLookup:Enabled"] = "true",
            ["Demo:WeatherLookup:Mode"] = mode,
            ["Demo:WeatherLookup:DelayMilliseconds"] = "0",
            ["Telemetry:CaptureSensitiveData"] = "true",
            ["Telemetry:UseStreaming"] = streaming.ToString(),
        }).Build();

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
            Assert.Equal("WeatherAdvisor", span.GetTagItem("gen_ai.prompt.name"));
            Assert.StartsWith("sha256:", Assert.IsType<string>(span.GetTagItem("gen_ai.prompt.version")));
            Assert.StartsWith("sha256:", Assert.IsType<string>(span.GetTagItem("gen_ai.agent.version")));
            Assert.Equal("synthetic", span.GetTagItem("travel_planner.tools.data.kind"));
            foreach (var spanEvent in span.Events)
            {
                Assert.Equal(span.GetTagItem("gen_ai.agent.version"),
                    spanEvent.Tags.Single(tag => tag.Key == "gen_ai.agent.version").Value);
            }
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

    private sealed class ResponsesHandler(
        List<JsonElement> requests, bool readableReasoning = false, bool retryTool = false,
        bool unavailable = false, bool endlessTools = false, bool responseLifecycle = false,
        string? terminal = null) : HttpMessageHandler
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
            var items = count == 1 || (retryTool && count == 2) || endlessTools
                ? new[]
                {
                    $$"""{"type":"reasoning","id":"rs_1","summary":{{summary}},"encrypted_content":"encrypted-demo-reasoning"}""",
                    $$"""{"type":"function_call","id":"fc_{{count}}","call_id":"call_{{count}}","name":"GetWeatherForecast","arguments":"{\"destination\":\"Lisbon\",\"date\":\"2026-07-12\"}","status":"completed"}""",
                }
                : new[]
                {
                    $$"""{"type":"message","id":"msg_1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"{{(unavailable ? "Forecast unavailable; advice is general." : "Pack a light jacket.")}}","annotations":[]}]}""",
                };
            var response = $$$"""
                {"id":"resp_{{{count}}}","object":"response","created_at":1791620000,"status":"completed",
                 "store":true,"model":"test-model","error":null,"output":[{{{string.Join(",", items)}}}],
                 "usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}
                """.ReplaceLineEndings(" ");
            if (terminal is not null)
            {
                var terminalResponse = JsonNode.Parse(response)!;
                terminalResponse["status"] = terminal;
                terminalResponse["output"] = new JsonArray();
                if (terminal == "failed")
                {
                    terminalResponse["error"] = new JsonObject { ["code"] = "server_error", ["message"] = "Synthetic failure." };
                }
                response = terminalResponse.ToJsonString();
            }

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
            var initialResponse = JsonNode.Parse(response)!;
            if (responseLifecycle)
            {
                initialResponse["status"] = "queued";
            }
            Emit("response.created", $$$"""{"type":"response.created","sequence_number":{{{sequence++}}},"response":{{{initialResponse.ToJsonString()}}}}""");
            if (responseLifecycle)
            {
                initialResponse["status"] = "in_progress";
                for (var duplicate = 0; duplicate < 2; duplicate++)
                {
                    Emit("response.in_progress", $$$"""{"type":"response.in_progress","sequence_number":{{{sequence++}}},"response":{{{initialResponse.ToJsonString()}}}}""");
                }
            }
            for (var i = 0; i < items.Length; i++)
            {
                var item = JsonNode.Parse(items[i])!;
                if (item["type"]?.GetValue<string>() == "message")
                {
                    Emit("response.output_text.delta", new JsonObject
                    {
                        ["type"] = "response.output_text.delta",
                        ["sequence_number"] = sequence++,
                        ["item_id"] = "msg_1",
                        ["output_index"] = i,
                        ["content_index"] = 0,
                        ["delta"] = item["content"]![0]!["text"]!.GetValue<string>(),
                    }.ToJsonString());
                }
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

    private sealed class ChatCompletionsHandler(List<string> requests, bool weatherRecovery = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("demo.openai.azure.com", request.RequestUri!.Host);
            Assert.Equal("/openai/deployments/router/chat/completions", request.RequestUri.AbsolutePath);
            requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (weatherRecovery && requests.Count <= 2)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$$"""
                        {"id":"chatcmpl_{{{requests.Count}}}","object":"chat.completion","created":1791620000,"model":"router",
                         "choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[
                          {"id":"call_{{{requests.Count}}}","type":"function","function":{"name":"GetWeatherForecast","arguments":"{\"destination\":\"Lisbon\",\"date\":\"2026-11-12\"}"}}
                         ]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                        """, Encoding.UTF8, "application/json"),
                };
            }
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
