using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MultiAgentTravelPlanner.Observability;

public sealed record AgentTraceProvenance(string PromptName, string PromptVersion, string AgentVersion)
{
    public static AgentTraceProvenance Create(
        string agentName,
        string instructions,
        string deployment,
        IList<AITool>? tools,
        ReasoningOptions? reasoning,
        bool useResponsesApi)
    {
        var promptVersion = Hash(instructions);
        var configuration = JsonSerializer.Serialize(new
        {
            agentName,
            promptVersion,
            deployment,
            api = useResponsesApi ? "responses" : "chat_completions",
            reasoningEffort = reasoning?.Effort?.ToString(),
            reasoningOutput = reasoning?.Output?.ToString(),
            tools = tools?.OfType<AIFunction>().Select(tool => new
            {
                tool.Name,
                tool.Description,
                tool.JsonSchema,
            }).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray(),
        });
        return new(agentName, promptVersion, Hash(configuration));
    }

    public void Apply(Activity? activity)
    {
        activity?.SetTag("gen_ai.prompt.name", PromptName);
        activity?.SetTag("gen_ai.prompt.version", PromptVersion);
        activity?.SetTag("gen_ai.agent.version", AgentVersion);
        activity?.SetTag("travel_planner.provenance.schema.version", "1");
        activity?.SetTag("travel_planner.tools.data.kind", "synthetic");
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}