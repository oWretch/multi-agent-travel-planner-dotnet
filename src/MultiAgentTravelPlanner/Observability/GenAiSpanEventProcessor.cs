using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTelemetry;

namespace MultiAgentTravelPlanner.Observability;

public sealed class GenAiSpanEventProcessor(bool captureSensitiveData) : BaseProcessor<Activity>
{
    public const string InferenceEventName = "gen_ai.client.inference.operation.details";
    public const string AgentEventName = "travel_planner.agent.operation.details";
    public const string ToolEventName = "travel_planner.tool.operation.details";

    private static readonly string[] MetadataAttributes =
    [
        "gen_ai.operation.name",
        "gen_ai.provider.name",
        "gen_ai.conversation.id",
        "gen_ai.agent.id",
        "gen_ai.agent.name",
        "gen_ai.agent.description",
        "gen_ai.agent.version",
        "gen_ai.prompt.name",
        "gen_ai.prompt.version",
        "travel_planner.provenance.schema.version",
        "travel_planner.tools.data.kind",
        "gen_ai.request.model",
        "gen_ai.request.stream",
        "gen_ai.request.previous_response.id",
        "gen_ai.request.reasoning.level",
        "gen_ai.response.id",
        "gen_ai.response.model",
        "gen_ai.response.status",
        "gen_ai.response.finish_reasons",
        "gen_ai.tool.name",
        "gen_ai.tool.type",
        "gen_ai.tool.call.id",
        "error.type",
    ];

    private static readonly string[] ContentAttributes =
    [
        "gen_ai.system_instructions",
        "gen_ai.input.messages",
        "gen_ai.output.messages",
        "gen_ai.tool.definitions",
        "gen_ai.tool.call.arguments",
        "gen_ai.tool.call.result",
    ];

    public override void OnEnd(Activity activity)
    {
        if (!activity.IsAllDataRequested)
        {
            return;
        }

        var eventName = activity.GetTagItem("gen_ai.operation.name") switch
        {
            "chat" => InferenceEventName,
            "invoke_agent" => AgentEventName,
            "execute_tool" => ToolEventName,
            _ => null,
        };

        if (eventName is null || activity.Events.Any(e => e.Name == eventName))
        {
            return;
        }

        var tags = new ActivityTagsCollection();
        CopyAttributes(MetadataAttributes);
        if (captureSensitiveData)
        {
            NormalizeReasoning(activity, "gen_ai.input.messages");
            NormalizeReasoning(activity, "gen_ai.output.messages");
            CopyAttributes(ContentAttributes);
        }

        // Reuse the SDK's GenAI JSON, including reasoning parts, without serializing provider state.
        activity.AddEvent(new ActivityEvent(
            eventName,
            new DateTimeOffset(activity.StartTimeUtc + activity.Duration),
            tags));

        void CopyAttributes(IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                if (activity.GetTagItem(name) is { } value)
                {
                    tags.Add(name, value);
                }
            }
        }
    }

    // The SDK serializes reasoning that has no text (provider-encrypted state only) as typed JSON with
    // base64-like protectedData. Drop those parts and keep readable reasoning as {"type":"reasoning","content":"..."}.
    private static void NormalizeReasoning(Activity activity, string tag)
    {
        if (activity.GetTagItem(tag) is not string json || !json.Contains("TextReasoningContent", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonArray messages)
            {
                return;
            }

            foreach (var message in messages.OfType<JsonObject>())
            {
                if (message["parts"] is not JsonArray parts)
                {
                    continue;
                }

                for (var i = parts.Count - 1; i >= 0; i--)
                {
                    if (parts[i] is not JsonObject part
                        || part["type"]?.GetValue<string>() is not { } type
                        || !type.EndsWith("TextReasoningContent", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var text = (part["content"] as JsonObject)?["text"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        parts.RemoveAt(i);
                    }
                    else
                    {
                        parts[i] = new JsonObject { ["type"] = "reasoning", ["content"] = text };
                    }
                }
            }

            activity.SetTag(tag, messages.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            activity.SetTag(tag, null);
        }
    }
}
