using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tests;

public sealed class GenAiTelemetryChatClientTests
{
    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public async Task RecordsExactReasoningLevelAndProviderUsage(ReasoningEffort effort, string expected)
    {
        using var capture = new TelemetryCapture();
        using var client = CreateClient(new StubChatClient());
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")], new ChatOptions
        {
            Reasoning = new ReasoningOptions { Effort = effort },
            ConversationId = "resp_previous",
            StopSequences = ["done", "a\"b"],
        });

        var span = Assert.Single(capture.Activities);
        Assert.Equal(expected, span.GetTagItem("gen_ai.request.reasoning.level"));
        Assert.Equal(3L, span.GetTagItem("gen_ai.usage.reasoning.output_tokens"));
        Assert.Equal(2L, Convert.ToInt64(span.GetTagItem("gen_ai.usage.cache_read.input_tokens")));
        Assert.Equal("resp_previous", span.GetTagItem("gen_ai.request.previous_response.id"));
        Assert.Equal("demo-session", span.GetTagItem("gen_ai.conversation.id"));
        Assert.Equal(new[] { "done", "a\"b" }, Assert.IsType<string[]>(span.GetTagItem("gen_ai.request.stop_sequences")));
        Assert.Equal(new[] { "stop" }, Assert.IsType<string[]>(span.GetTagItem("gen_ai.response.finish_reasons")));
        Assert.Equal(false, span.GetTagItem("gen_ai.request.stream"));
        Assert.Null(span.GetTagItem("gen_ai.response.time_to_first_chunk"));
    }

    [Fact]
    public async Task BuffersStreamingAndMeasuresFirstChunkInSeconds()
    {
        using var capture = new TelemetryCapture();
        var firstChunkMetrics = new List<double>();
        using var metrics = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentCatalog.TelemetrySourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        metrics.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "gen_ai.client.operation.time_to_first_chunk")
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "gen_ai.request.model" && tag.Value is "streaming-test")
                    {
                        firstChunkMetrics.Add(value);
                    }
                }
            }
        });
        metrics.Start();
        using var client = new StreamingChatClient(CreateClient(new StubChatClient()));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions { ModelId = "streaming-test" });

        Assert.Equal("Hello", response.Text);
        Assert.Equal(3, response.Usage?.ReasoningTokenCount);
        var span = Assert.Single(capture.Activities);
        Assert.Equal(true, span.GetTagItem("gen_ai.request.stream"));
        var firstChunkSeconds = Assert.IsType<double>(span.GetTagItem("gen_ai.response.time_to_first_chunk"));
        Assert.True(firstChunkSeconds >= 0.015);
        Assert.True(firstChunkSeconds <= span.Duration.TotalSeconds);
        Assert.Equal(3L, span.GetTagItem("gen_ai.usage.reasoning.output_tokens"));
        Assert.True(Assert.Single(firstChunkMetrics) >= 0.015);
    }

    [Fact]
    public async Task DoesNotInventReasoningOrUsageOrEmptyStreamTiming()
    {
        using var capture = new TelemetryCapture();
        using var client = CreateClient(new StubChatClient(empty: true));
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "Hello")])) { }

        Assert.Equal(2, capture.Activities.Count);
        foreach (var span in capture.Activities)
        {
            Assert.Null(span.GetTagItem("gen_ai.request.reasoning.level"));
            Assert.Null(span.GetTagItem("gen_ai.usage.reasoning.output_tokens"));
            Assert.Null(span.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
            Assert.Null(span.GetTagItem("gen_ai.response.time_to_first_chunk"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesErrorsAndCancellation(bool cancel)
    {
        using var capture = new TelemetryCapture();
        using var client = new StreamingChatClient(CreateClient(new StubChatClient(fail: true, cancel: cancel)));
        var exception = await Record.ExceptionAsync(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]));

        Assert.NotNull(exception);
        Assert.Equal(cancel ? typeof(OperationCanceledException) : typeof(InvalidOperationException), exception.GetType());
        var span = Assert.Single(capture.Activities);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(exception.GetType().FullName, span.GetTagItem("error.type"));
        Assert.Equal(new[] { "error" }, Assert.IsType<string[]>(span.GetTagItem("gen_ai.response.finish_reasons")));
        Assert.NotNull(span.GetTagItem("gen_ai.response.time_to_first_chunk"));
        Assert.Null(span.GetTagItem("gen_ai.usage.reasoning.output_tokens"));
    }

    [Fact]
    public async Task KeepsHostedConversationIdAndDoesNotMistakeChatHistoryForPreviousResponse()
    {
        using var capture = new TelemetryCapture();
        using var responses = CreateClient(new StubChatClient());
        using var chat = CreateClient(new StubChatClient(), useResponsesApi: false);
        await responses.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions { ConversationId = "conv_hosted" });
        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions { ConversationId = "chat-history" });

        Assert.Equal(2, capture.Activities.Count);
        Assert.Contains(capture.Activities, a => a.GetTagItem("gen_ai.conversation.id") is "conv_hosted");
        Assert.Contains(capture.Activities, a => a.GetTagItem("gen_ai.conversation.id") is "chat-history");
        Assert.All(capture.Activities, a => Assert.Null(a.GetTagItem("gen_ai.request.previous_response.id")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContentEventsCaptureReadableReasoningOnlyWhenEnabled(bool streaming, bool captureSensitiveData)
    {
        using var capture = new TelemetryCapture(captureSensitiveData);
        using var instrumented = CreateClient(new StubChatClient(reasoning: true),
            captureSensitiveData: captureSensitiveData);
        using var client = streaming ? new StreamingChatClient(instrumented) : null;
        var response = await (client ?? instrumented).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Lisbon?")],
            new ChatOptions { Instructions = "Use the forecast." });

        Assert.Equal("Hello", response.Text);
        var span = Assert.Single(capture.Activities);
        var tags = Assert.Single(span.Events).Tags.ToDictionary();
        if (!captureSensitiveData)
        {
            Assert.False(tags.ContainsKey("gen_ai.system_instructions"));
            Assert.False(tags.ContainsKey("gen_ai.input.messages"));
            Assert.False(tags.ContainsKey("gen_ai.output.messages"));
            return;
        }

        var outputJson = Assert.IsType<string>(tags["gen_ai.output.messages"]);
        using var output = JsonDocument.Parse(outputJson);
        var parts = Assert.Single(output.RootElement.EnumerateArray()).GetProperty("parts").EnumerateArray().ToArray();
        var reasoning = parts.Where(p => p.GetProperty("type").GetString() == "reasoning").ToArray();
        Assert.Equal(streaming ? 2 : 1, reasoning.Length);
        Assert.Equal("Check the forecast.", string.Concat(reasoning.Select(p => p.GetProperty("content").GetString())));
        Assert.Equal("Hello", Assert.Single(parts, p => p.GetProperty("type").GetString() == "text")
            .GetProperty("content").GetString());
        Assert.DoesNotContain("protected-provider-state", outputJson);
        Assert.Equal(span.GetTagItem("gen_ai.system_instructions"), tags["gen_ai.system_instructions"]);
        Assert.Equal(span.GetTagItem("gen_ai.input.messages"), tags["gen_ai.input.messages"]);
    }

    private static IChatClient CreateClient(
        IChatClient inner, bool useResponsesApi = true, bool captureSensitiveData = false) =>
        new OpenTelemetryChatClient(
            new GenAiTelemetryChatClient(inner, useResponsesApi),
            sourceName: AgentCatalog.TelemetrySourceName)
        {
            EnableSensitiveData = captureSensitiveData,
        };

    private sealed class StubChatClient(
        bool empty = false, bool fail = false, bool cancel = false, bool reasoning = false) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reasoning
                ? [new TextReasoningContent("Check the forecast.") { ProtectedData = "protected-provider-state" }, new TextContent("Hello")]
                : [new TextContent("Hello")]))
            {
                FinishReason = ChatFinishReason.Stop,
                Usage = empty ? null : CreateUsage(),
            });

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (empty)
            {
                yield break;
            }

            await Task.Delay(25, cancellationToken);
            if (reasoning)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new TextReasoningContent("Check ") { ProtectedData = "protected-provider-state" }]);
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("the forecast.")]);
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello");
            if (fail)
            {
                if (cancel)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new InvalidOperationException("provider failed");
            }

            await Task.Delay(50, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(CreateUsage())])
            {
                FinishReason = ChatFinishReason.Stop,
            };
        }

        private static UsageDetails CreateUsage() => new()
        {
            InputTokenCount = 10,
            OutputTokenCount = 5,
            TotalTokenCount = 15,
            CachedInputTokenCount = 2,
            ReasoningTokenCount = 3,
        };

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
