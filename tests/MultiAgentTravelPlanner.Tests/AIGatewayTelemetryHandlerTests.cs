using System.Diagnostics;
using System.Net;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tests;

[Collection("AI gateway")]
public sealed class AIGatewayTelemetryHandlerTests
{
    [Theory]
    [InlineData("hit", false)]
    [InlineData("miss", true)]
    [InlineData("bypass", true)]
    public async Task RecordsCacheOutcomeAndPropagatesAgentContext(string cacheStatus, bool backendRequest)
    {
        Activity? gatewayActivity = null;
        using var listener = CreateListener(activity => gatewayActivity = activity);
        using var source = new ActivitySource(nameof(AIGatewayTelemetryHandlerTests));
        using var root = source.StartActivity("POST /api/chat");
        root!.SetTag("travel_planner.routing.mode", "fixed");
        using var agent = source.StartActivity("invoke_agent WeatherAdvisor");
        agent!.SetTag("gen_ai.agent.name", "WeatherAdvisor");
        using var model = source.StartActivity("responses");
        using var client = new HttpClient(new AIGatewayTelemetryHandler
        {
            InnerHandler = new StubHandler(request =>
            {
                Assert.Equal("WeatherAdvisor", request.Headers.GetValues("x-agent-name").Single());
                Assert.Equal("fixed", request.Headers.GetValues("x-routing-mode").Single());
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("x-ai-cache", cacheStatus);
                return response;
            }),
        });

        using var response = await client.PostAsync("https://demo.azure-api.net/openai/v1/responses", null);

        Assert.NotNull(gatewayActivity);
        Assert.Equal(root.TraceId, gatewayActivity.TraceId);
        Assert.Equal(model!.SpanId, gatewayActivity.ParentSpanId);
        Assert.Equal(cacheStatus, gatewayActivity.GetTagItem("ai.gateway.cache.status"));
        Assert.Equal(backendRequest, gatewayActivity.GetTagItem("ai.gateway.backend.request"));
        Assert.Equal("WeatherAdvisor", gatewayActivity.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("fixed", gatewayActivity.GetTagItem("travel_planner.routing.mode"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ActivityStatusCode.Unset)]
    [InlineData(HttpStatusCode.TooManyRequests, ActivityStatusCode.Error)]
    public async Task DoesNotInventCacheOutcomeWhenHeaderIsAbsent(
        HttpStatusCode statusCode,
        ActivityStatusCode activityStatus)
    {
        Activity? gatewayActivity = null;
        using var listener = CreateListener(activity => gatewayActivity = activity);
        using var client = new HttpClient(new AIGatewayTelemetryHandler
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(statusCode)),
        });

        using var response = await client.PostAsync("https://demo.azure-api.net/openai/responses", null);

        Assert.NotNull(gatewayActivity);
        Assert.Null(gatewayActivity.GetTagItem("ai.gateway.cache.status"));
        Assert.Null(gatewayActivity.GetTagItem("ai.gateway.backend.request"));
        Assert.Equal(activityStatus, gatewayActivity.Status);
        Assert.Equal((int)statusCode, gatewayActivity.GetTagItem("http.response.status_code"));
    }

    [Fact]
    public async Task RecordsAndRethrowsTransportFailures()
    {
        Activity? gatewayActivity = null;
        using var listener = CreateListener(activity => gatewayActivity = activity);
        using var client = new HttpClient(new AIGatewayTelemetryHandler
        {
            InnerHandler = new StubHandler(_ => throw new HttpRequestException("Gateway unavailable")),
        });

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.PostAsync("https://demo.azure-api.net/openai/responses", null));

        Assert.NotNull(gatewayActivity);
        Assert.Equal(ActivityStatusCode.Error, gatewayActivity.Status);
        Assert.Equal(typeof(HttpRequestException).FullName, gatewayActivity.GetTagItem("error.type"));
    }

    private static ActivityListener CreateListener(Action<Activity> onGatewayStopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AIGatewayTelemetryHandler.SourceName
                || source.Name == nameof(AIGatewayTelemetryHandlerTests),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.Source.Name == AIGatewayTelemetryHandler.SourceName)
                {
                    onGatewayStopped(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
