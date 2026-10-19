using System.Diagnostics;
using System.Text.Json;
using OpenTelemetry;

namespace MultiAgentTravelPlanner.Observability;

public sealed class ConversationIdActivityProcessor : BaseProcessor<Activity>
{
    public const string ConversationAttributeName = "gen_ai.conversation.id";
    public const string SessionAttributeName = "session.id";
    private const string OperationAttributeName = "gen_ai.operation.name";

    public override void OnEnd(Activity activity)
    {
        var operation = activity.GetTagItem(OperationAttributeName) as string;
        if (operation is null && activity.GetTagItem("travel_planner.lookup.id") is null)
        {
            return;
        }

        if (activity.GetTagItem(ConversationAttributeName) is null
            && FindTag(activity, SessionAttributeName) is { } sessionId)
        {
            activity.SetTag(ConversationAttributeName, sessionId);
        }

        foreach (var tag in new[]
        {
            "gen_ai.agent.id", "gen_ai.agent.name", "gen_ai.agent.description",
            "gen_ai.agent.version", "gen_ai.prompt.name", "gen_ai.prompt.version",
            "travel_planner.provenance.schema.version", "travel_planner.tools.data.kind",
            "gen_ai.tool.call.id",
        })
        {
            if (activity.GetTagItem(tag) is null && FindTag(activity.Parent, tag) is { } value)
            {
                activity.SetTag(tag, value);
            }
        }

        if (activity.GetTagItem("gen_ai.response.finish_reasons") is string reasons)
        {
            activity.SetTag("gen_ai.response.finish_reasons", JsonSerializer.Deserialize<string[]>(reasons));
        }

        if (activity.Status == ActivityStatusCode.Error)
        {
            if (operation == "execute_tool")
            {
                activity.SetTag("gen_ai.tool.call.result", null);
            }
            else if (operation == "chat" && activity.GetTagItem("gen_ai.response.finish_reasons") is null)
            {
                activity.SetTag("gen_ai.response.finish_reasons", new[] { "error" });
            }
        }
    }

    public static ActivityTagsCollection CorrelateEvent(Activity? activity, ActivityTagsCollection tags)
    {
        foreach (var name in new[]
        {
            ConversationAttributeName, "gen_ai.agent.id", "gen_ai.agent.name", "gen_ai.agent.version",
            "gen_ai.prompt.name", "gen_ai.prompt.version", "gen_ai.tool.call.id",
        })
        {
            if (FindTag(activity, name) is { } value)
            {
                tags[name] = value;
            }
        }
        if (!tags.ContainsKey(ConversationAttributeName) && FindTag(activity, SessionAttributeName) is { } sessionId)
        {
            tags[ConversationAttributeName] = sessionId;
        }
        return tags;
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
