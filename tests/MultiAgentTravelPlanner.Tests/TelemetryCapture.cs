using System.Collections.Concurrent;
using System.Diagnostics;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tests;

internal sealed class TelemetryCapture : IDisposable
{
    private readonly Activity root = new Activity("telemetry test")
        .SetIdFormat(ActivityIdFormat.W3C).Start();
    private readonly ActivityListener listener;
    private readonly ConversationIdActivityProcessor processor = new();
    private readonly GenAiSpanEventProcessor eventProcessor;

    public ConcurrentQueue<Activity> Activities { get; } = new();

    public TelemetryCapture(bool captureSensitiveData = true)
    {
        eventProcessor = new GenAiSpanEventProcessor(captureSensitiveData);
        root.SetTag(ConversationIdActivityProcessor.SessionAttributeName, "demo-session");
        listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AgentCatalog.TelemetrySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == root.TraceId)
                {
                    processor.OnEnd(activity);
                    eventProcessor.OnEnd(activity);
                    Activities.Enqueue(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
    }

    public void Dispose()
    {
        listener.Dispose();
        processor.Dispose();
        eventProcessor.Dispose();
        root.Dispose();
    }
}
