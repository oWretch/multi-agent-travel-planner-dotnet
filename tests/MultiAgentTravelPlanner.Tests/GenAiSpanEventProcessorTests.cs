using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Monitor.OpenTelemetry.Exporter;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Observability;
using MultiAgentTravelPlanner.Tools;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace MultiAgentTravelPlanner.Tests;

[CollectionDefinition("AzureMonitorExporter", DisableParallelization = true)]
public sealed class AzureMonitorExporterCollection;

[Collection("AzureMonitorExporter")]
public sealed class GenAiSpanEventProcessorTests
{
    [Theory]
    [InlineData("chat", GenAiSpanEventProcessor.InferenceEventName)]
    [InlineData("invoke_agent", GenAiSpanEventProcessor.AgentEventName)]
    [InlineData("execute_tool", GenAiSpanEventProcessor.ToolEventName)]
    public void RecordsOneCompletionEventWithExistingContentAndContext(string operation, string eventName)
    {
        using var activity = new Activity(operation).Start();
        activity.SetTag("gen_ai.operation.name", operation);
        activity.SetTag("gen_ai.agent.name", "WeatherAdvisor");
        activity.SetTag("gen_ai.conversation.id", "conversation-1");
        activity.SetTag("gen_ai.system_instructions", """[{"type":"text","content":"Use the forecast."}]""");
        activity.SetTag("gen_ai.input.messages", """[{"role":"user","parts":[{"type":"text","content":"Lisbon?"}]}]""");
        activity.SetTag("gen_ai.output.messages", """[{"role":"assistant","parts":[{"type":"text","content":"Sunny."}]}]""");
        activity.SetTag("gen_ai.tool.call.arguments", """{"destination":"Lisbon"}""");
        activity.SetTag("gen_ai.tool.call.result", """{"forecast":"Sunny"}""");
        activity.SetTag("unrelated.secret", "do not copy");
        activity.Stop();
        using var processor = new GenAiSpanEventProcessor(captureSensitiveData: true);

        processor.OnEnd(activity);
        processor.OnEnd(activity);

        var spanEvent = Assert.Single(activity.Events);
        Assert.Equal(eventName, spanEvent.Name);
        Assert.Equal(new DateTimeOffset(activity.StartTimeUtc + activity.Duration), spanEvent.Timestamp);
        var tags = spanEvent.Tags.ToDictionary();
        foreach (var name in new[]
        {
            "gen_ai.operation.name", "gen_ai.agent.name", "gen_ai.conversation.id",
            "gen_ai.system_instructions", "gen_ai.input.messages", "gen_ai.output.messages",
            "gen_ai.tool.call.arguments", "gen_ai.tool.call.result",
        })
        {
            Assert.Equal(activity.GetTagItem(name), tags[name]);
        }
        Assert.False(tags.ContainsKey("unrelated.secret"));
    }

    [Fact]
    public void ReasoningIsPlainTextAndProviderStateIsDropped()
    {
        using var activity = new Activity("invoke_agent").Start();
        activity.SetTag("gen_ai.operation.name", "invoke_agent");
        activity.SetTag("gen_ai.output.messages", """
            [{"role":"assistant","parts":[
              {"type":"Microsoft.Extensions.AI.TextReasoningContent","content":{"text":"","protectedData":"enc-state"}},
              {"type":"Microsoft.Extensions.AI.TextReasoningContent","content":{"text":"Check the forecast.","protectedData":"enc-state"}},
              {"type":"text","content":"Sunny."}]}]
            """);
        activity.Stop();
        using var processor = new GenAiSpanEventProcessor(captureSensitiveData: true);

        processor.OnEnd(activity);

        var eventJson = (string)Assert.Single(activity.Events).Tags.ToDictionary()["gen_ai.output.messages"]!;
        Assert.Equal(eventJson, activity.GetTagItem("gen_ai.output.messages"));
        Assert.DoesNotContain("enc-state", eventJson);
        Assert.DoesNotContain("TextReasoningContent", eventJson);
        var parts = JsonDocument.Parse(eventJson).RootElement[0].GetProperty("parts");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("reasoning", parts[0].GetProperty("type").GetString());
        Assert.Equal("Check the forecast.", parts[0].GetProperty("content").GetString());
    }

    [Fact]
    public void DisabledSensitiveCaptureEmitsOnlyMetadataEvenIfContentTagsExist()
    {
        using var activity = new Activity("chat").Start();
        activity.SetTag("gen_ai.operation.name", "chat");
        foreach (var name in new[]
        {
            "gen_ai.system_instructions", "gen_ai.input.messages", "gen_ai.output.messages",
            "gen_ai.tool.definitions", "gen_ai.tool.call.arguments", "gen_ai.tool.call.result",
            "gen_ai.prompt.variable.secret",
        })
        {
            activity.SetTag(name, "sensitive");
        }
        activity.Stop();

        using var processor = new GenAiSpanEventProcessor(captureSensitiveData: false);
        processor.OnEnd(activity);

        var tag = Assert.Single(Assert.Single(activity.Events).Tags);
        Assert.Equal("gen_ai.operation.name", tag.Key);
        Assert.Equal("chat", tag.Value);
    }

    [Theory]
    [InlineData("database", true)]
    [InlineData("chat", false)]
    public void IgnoresUnrelatedOrUnrecordedActivities(string operation, bool allDataRequested)
    {
        using var activity = new Activity(operation).Start();
        activity.SetTag("gen_ai.operation.name", operation);
        activity.IsAllDataRequested = allDataRequested;
        activity.Stop();
        using var processor = new GenAiSpanEventProcessor(captureSensitiveData: true);

        processor.OnEnd(activity);

        Assert.Empty(activity.Events);
    }

    [Fact]
    public void FailedToolEventRetainsArgumentsAndErrorButNotResult()
    {
        using var activity = new Activity("execute_tool").Start();
        activity.SetTag("gen_ai.operation.name", "execute_tool");
        activity.SetTag("gen_ai.tool.call.arguments", """{"destination":"Lisbon"}""");
        activity.SetTag("gen_ai.tool.call.result", "null");
        activity.SetTag("error.type", "System.InvalidOperationException");
        activity.SetStatus(ActivityStatusCode.Error);
        activity.Stop();
        using var contextProcessor = new ConversationIdActivityProcessor();
        using var eventProcessor = new GenAiSpanEventProcessor(captureSensitiveData: true);

        contextProcessor.OnEnd(activity);
        eventProcessor.OnEnd(activity);

        var tags = Assert.Single(activity.Events).Tags.ToDictionary();
        Assert.Equal("System.InvalidOperationException", tags["error.type"]);
        Assert.Equal(activity.GetTagItem("gen_ai.tool.call.arguments"), tags["gen_ai.tool.call.arguments"]);
        Assert.False(tags.ContainsKey("gen_ai.tool.call.result"));
        Assert.False(tags.ContainsKey("gen_ai.output.messages"));
    }

    [Fact]
    public void AzureMonitorExportsContentEventAsCorrelatedTrace()
    {
        using var handler = new ExportHandler();
        using var httpClient = new HttpClient(handler);
        using var source = new ActivitySource(nameof(AzureMonitorExportsContentEventAsCorrelatedTrace));
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new ConversationIdActivityProcessor())
            .AddProcessor(new GenAiSpanEventProcessor(captureSensitiveData: true))
            .AddProcessor(new SimpleActivityExportProcessor(new AzureMonitorTraceExporter(new AzureMonitorExporterOptions
            {
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=https://example.invalid",
                Transport = new HttpClientTransport(httpClient),
                DisableOfflineStorage = true,
            })))
            .Build();
        using var root = new Activity("request").Start();
        root.SetTag(ConversationIdActivityProcessor.SessionAttributeName, "conversation-1");
        using var activity = source.StartActivity("chat", ActivityKind.Client);
        Assert.NotNull(activity);
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.provider.name", "openai");
        activity.SetTag("gen_ai.input.messages", """[{"role":"user","parts":[{"type":"text","content":"Lisbon?"}]}]""");
        activity.Stop();

        var payloadJson = Assert.Single(handler.Payloads);
        var items = payloadJson.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var item = JsonDocument.Parse(line);
            return item.RootElement.Clone();
        });
        var trace = Assert.Single(items, item =>
            item.GetProperty("data").GetProperty("baseType").GetString() == "MessageData");
        var data = trace.GetProperty("data").GetProperty("baseData");
        Assert.Equal(GenAiSpanEventProcessor.InferenceEventName, data.GetProperty("message").GetString());
        var properties = data.GetProperty("properties");
        Assert.Equal("conversation-1", properties.GetProperty("gen_ai.conversation.id").GetString());
        Assert.Equal(activity.GetTagItem("gen_ai.input.messages"), properties.GetProperty("gen_ai.input.messages").GetString());
        Assert.Equal(activity.TraceId.ToHexString(), trace.GetProperty("tags").GetProperty("ai.operation.id").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), trace.GetProperty("tags").GetProperty("ai.operation.parentId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureMonitorExportsLookupHistoryWithoutRequiringContentCapture(bool captureSensitiveData)
    {
        using var handler = new ExportHandler();
        using var httpClient = new HttpClient(handler);
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(AgentCatalog.TelemetrySourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new ConversationIdActivityProcessor())
            .AddProcessor(new GenAiSpanEventProcessor(captureSensitiveData))
            .AddProcessor(new SimpleActivityExportProcessor(new AzureMonitorTraceExporter(new AzureMonitorExporterOptions
            {
                ConnectionString = $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://example.invalid",
                Transport = new HttpClientTransport(httpClient),
                DisableOfflineStorage = true,
            })))
            .Build();
        using var root = new Activity("execute_tool GetWeatherForecast").SetIdFormat(ActivityIdFormat.W3C).Start();
        root.SetTag("session.id", "lifecycle-session");
        root.SetTag("gen_ai.agent.name", "WeatherAdvisor");
        root.SetTag("gen_ai.agent.version", "sha256:test-version");
        root.SetTag("gen_ai.tool.call.id", "call_weather");
        var lookup = new WeatherLookup("fail-once", TimeSpan.Zero);
        using var run = lookup.BeginRun();
        await lookup.GetForecastAsync("private-destination", "2026-11-12");
        await lookup.GetForecastAsync("private-destination", "2026-11-12");

        var items = handler.Payloads.SelectMany(payload => payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .Where(item => item.TryGetProperty("tags", out var tags)
                && tags.TryGetProperty("ai.operation.id", out var operation)
                && operation.GetString() == root.TraceId.ToHexString())
            .ToArray();
        var transitions = items.Where(item => item.GetProperty("data").GetProperty("baseType").GetString() == "MessageData")
            .Select(item => item.GetProperty("data").GetProperty("baseData"))
            .Where(data => data.GetProperty("message").GetString() == WeatherLookup.TransitionEventName).ToArray();
        Assert.Equal(6, transitions.Length);
        Assert.Equal(new[] { "pending", "running", "failed", "pending", "running", "succeeded" },
            transitions.Select(data => data.GetProperty("properties").GetProperty("travel_planner.lookup.state").GetString()));
        Assert.All(transitions, data =>
        {
            var properties = data.GetProperty("properties");
            Assert.Equal("lifecycle-session", properties.GetProperty("gen_ai.conversation.id").GetString());
            Assert.Equal("WeatherAdvisor", properties.GetProperty("gen_ai.agent.name").GetString());
            Assert.Equal("sha256:test-version", properties.GetProperty("gen_ai.agent.version").GetString());
            Assert.Equal("call_weather", properties.GetProperty("gen_ai.tool.call.id").GetString());
            Assert.DoesNotContain("private-destination", data.GetRawText());
        });
        Assert.Single(transitions.Select(data => data.GetProperty("properties")
            .GetProperty("travel_planner.lookup.id").GetString()).Distinct());
        Assert.All(items.Where(item => item.GetProperty("data").GetProperty("baseType").GetString() == "MessageData"),
            item => Assert.Contains(items, parent =>
                parent.GetProperty("data").GetProperty("baseType").GetString() == "RemoteDependencyData"
                && parent.GetProperty("data").GetProperty("baseData").GetProperty("id").GetString()
                    == item.GetProperty("tags").GetProperty("ai.operation.parentId").GetString()));
    }

    private sealed class ExportHandler : HttpMessageHandler
    {
        public List<string> Payloads { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Handle(request, cancellationToken);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Handle(request, cancellationToken));

        private HttpResponseMessage Handle(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            using var stream = new MemoryStream(bytes);
            using var gzip = request.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(stream, CompressionMode.Decompress)
                : null;
            using var reader = new StreamReader(gzip is null ? stream : gzip, Encoding.UTF8);
            Payloads.Add(reader.ReadToEnd());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"itemsReceived":2,"itemsAccepted":2,"errors":[]}"""),
            };
        }
    }
}
