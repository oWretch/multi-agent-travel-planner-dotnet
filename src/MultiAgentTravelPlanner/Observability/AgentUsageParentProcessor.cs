using System.Diagnostics;
using OpenTelemetry;

namespace MultiAgentTravelPlanner.Observability;

public sealed class AgentUsageParentProcessor : BaseProcessor<Activity>
{
    private const string OperationNameTag = "gen_ai.operation.name";
    private const string AgentNameTag = "gen_ai.agent.name";
    private const string InvokeAgentOperation = "invoke_agent";
    private const string CoordinatorAgent = "Coordinator";
    private const string UsagePrefix = "gen_ai.usage.";

    public override void OnEnd(Activity activity)
    {
        if (!HasTagValue(activity, OperationNameTag, InvokeAgentOperation)
            || !HasTagValue(activity, AgentNameTag, CoordinatorAgent))
        {
            return;
        }

        var requestActivity = FindRequestActivity(activity.Parent);
        if (requestActivity is null)
        {
            return;
        }

        foreach (var tag in activity.TagObjects)
        {
            if (IsTokenUsageTag(tag.Key))
            {
                requestActivity.SetTag(tag.Key, tag.Value);
            }
        }
    }

    private static Activity? FindRequestActivity(Activity? activity)
    {
        while (activity is not null)
        {
            if (activity.Kind == ActivityKind.Server)
            {
                return activity;
            }

            activity = activity.Parent;
        }

        return null;
    }

    private static bool HasTagValue(
        Activity activity,
        string key,
        string expectedValue) =>
        string.Equals(
            activity.GetTagItem(key)?.ToString(),
            expectedValue,
            StringComparison.Ordinal);

    private static bool IsTokenUsageTag(string key) =>
        key.StartsWith(UsagePrefix, StringComparison.Ordinal)
        && key.EndsWith("_tokens", StringComparison.Ordinal);
}
