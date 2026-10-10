using System.Diagnostics;

namespace MultiAgentTravelPlanner.Observability;

public sealed class AIGatewayTelemetryHandler : DelegatingHandler
{
    public const string SourceName = "MultiAgentTravelPlanner.AIGateway";
    private static readonly ActivitySource Source = new(SourceName);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var parent = Activity.Current;
        var agentName = FindTag(parent, "gen_ai.agent.name");
        var routingMode = FindTag(parent, "travel_planner.routing.mode");
        using var activity = Source.StartActivity("ai_gateway.request", ActivityKind.Client);
        activity?.SetTag("gen_ai.agent.name", agentName);
        activity?.SetTag("travel_planner.routing.mode", routingMode);
        activity?.SetTag("server.address", request.RequestUri?.Host);
        activity?.SetTag("http.request.method", request.Method.Method);
        SetHeader(request, "x-agent-name", agentName);
        SetHeader(request, "x-routing-mode", routingMode);

        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (response.Headers.TryGetValues("x-ai-cache", out var values))
            {
                var status = values.First();
                activity?.SetTag("ai.gateway.cache.status", status);
                activity?.SetTag("ai.gateway.backend.request", status != "hit");
            }

            if (!response.IsSuccessStatusCode)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            return response;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("error.type", ex.GetType().FullName);
            throw;
        }
    }

    private static string? FindTag(Activity? activity, string name)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (current.GetTagItem(name) is string value)
            {
                return value;
            }
        }

        return null;
    }

    private static void SetHeader(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
        {
            request.Headers.Remove(name);
            request.Headers.Add(name, value);
        }
    }
}
