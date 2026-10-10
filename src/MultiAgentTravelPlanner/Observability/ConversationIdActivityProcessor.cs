using System.Diagnostics;
using OpenTelemetry;

namespace MultiAgentTravelPlanner.Observability;

public sealed class ConversationIdActivityProcessor : BaseProcessor<Activity>
{
    public const string ConversationAttributeName = "gen_ai.conversation.id";
    public const string SessionAttributeName = "session.id";
    private const string OperationAttributeName = "gen_ai.operation.name";

    public override void OnEnd(Activity activity)
    {
        if (activity.GetTagItem(OperationAttributeName) is null
            || activity.GetTagItem(ConversationAttributeName) is not null)
        {
            return;
        }

        var sessionId = FindTag(activity, SessionAttributeName);
        if (sessionId is not null)
        {
            activity.SetTag(ConversationAttributeName, sessionId);
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
}
