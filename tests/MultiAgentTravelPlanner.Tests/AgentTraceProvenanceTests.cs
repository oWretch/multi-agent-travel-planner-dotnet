using System.Diagnostics;
using Microsoft.Extensions.AI;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentTraceProvenanceTests
{
    [Fact]
    public void FingerprintsAreStableAndDoNotExposeInstructions()
    {
        var provenance = Create();
        Assert.Equal(provenance, Create());
        using var activity = new Activity("invoke_agent").Start();

        provenance.Apply(activity);

        Assert.Equal("Coordinator", activity.GetTagItem("gen_ai.prompt.name"));
        Assert.Equal(provenance.PromptVersion, activity.GetTagItem("gen_ai.prompt.version"));
        Assert.Equal(provenance.AgentVersion, activity.GetTagItem("gen_ai.agent.version"));
        Assert.StartsWith("sha256:", provenance.PromptVersion);
        Assert.Equal(71, provenance.PromptVersion.Length);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains("private instructions") == true);
    }

    [Fact]
    public void EffectiveConfigurationChangesAgentVersion()
    {
        var baseline = Create();
        Assert.NotEqual(baseline.PromptVersion, Create(instructions: "Changed instructions").PromptVersion);
        Assert.NotEqual(baseline.AgentVersion, Create(instructions: "Changed instructions").AgentVersion);
        Assert.NotEqual(baseline.AgentVersion, Create(deployment: "router").AgentVersion);
        Assert.NotEqual(baseline.AgentVersion, Create(useResponsesApi: false).AgentVersion);
        Assert.NotEqual(baseline.AgentVersion, Create(effort: ReasoningEffort.High).AgentVersion);
        Assert.Equal(baseline.PromptVersion, Create(deployment: "router").PromptVersion);
    }

    private static AgentTraceProvenance Create(
        string instructions = "private instructions",
        string deployment = "test-model",
        bool useResponsesApi = true,
        ReasoningEffort? effort = null) =>
        AgentTraceProvenance.Create("Coordinator", instructions, deployment, null,
            new ReasoningOptions { Effort = effort ?? ReasoningEffort.Low }, useResponsesApi);

        [Fact]
        public void ToolDefinitionsChangeVersionButTheirOrderingDoesNot()
        {
            var weather = AIFunctionFactory.Create((string destination) => destination, name: "weather");
            var budget = AIFunctionFactory.Create((int days) => days, name: "budget");
            var original = AgentTraceProvenance.Create("Coordinator", "instructions", "model", new[] { weather, budget }, null, true);
            var reordered = AgentTraceProvenance.Create("Coordinator", "instructions", "model", new[] { budget, weather }, null, true);
            var changed = AgentTraceProvenance.Create("Coordinator", "instructions", "model", new[] { weather }, null, true);

            Assert.Equal(original, reordered);
            Assert.NotEqual(original.AgentVersion, changed.AgentVersion);
            Assert.Equal(original.PromptVersion, changed.PromptVersion);
        }

        [Theory]
        [InlineData("chat")]
        [InlineData("execute_tool")]
        public void ModelAndToolEventsInheritProvenanceWithoutContent(string operation)
        {
            var provenance = Create();
            using var agent = new Activity("invoke_agent").Start();
            provenance.Apply(agent);
            using var child = new Activity(operation).Start();
            child.SetTag("gen_ai.operation.name", operation);
            child.Stop();
            using var contextProcessor = new ConversationIdActivityProcessor();
            using var eventProcessor = new GenAiSpanEventProcessor(captureSensitiveData: false);

            contextProcessor.OnEnd(child);
            eventProcessor.OnEnd(child);

            Assert.Equal(provenance.AgentVersion, child.GetTagItem("gen_ai.agent.version"));
            var tags = Assert.Single(child.Events).Tags.ToDictionary();
            Assert.Equal(provenance.PromptVersion, tags["gen_ai.prompt.version"]);
            Assert.Equal("synthetic", tags["travel_planner.tools.data.kind"]);
            Assert.False(tags.ContainsKey("gen_ai.system_instructions"));
        }
}