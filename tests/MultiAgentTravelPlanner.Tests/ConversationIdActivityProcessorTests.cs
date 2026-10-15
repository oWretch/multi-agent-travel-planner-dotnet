using System.Diagnostics;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tests;

public sealed class ConversationIdActivityProcessorTests
{
    static ConversationIdActivityProcessorTests()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
    }

    [Fact]
    public void OnEnd_AddsConversationIdToGenAiSpan()
    {
        using var root = new Activity("POST /api/chat").Start();
        root.SetTag(ConversationIdActivityProcessor.SessionAttributeName, "chat-123");
        using var child = new Activity("invoke_agent Coordinator")
            .Start();
        child.SetTag("gen_ai.operation.name", "invoke_agent");
        child.Stop();

        new ConversationIdActivityProcessor().OnEnd(child);

        Assert.Equal(
            "chat-123",
            child.GetTagItem(
                ConversationIdActivityProcessor.ConversationAttributeName));
        Assert.Null(root.GetTagItem(
            ConversationIdActivityProcessor.ConversationAttributeName));
    }

    [Fact]
    public void OnEnd_DoesNotAddConversationIdToNonGenAiSpan()
    {
        using var root = new Activity("POST /api/chat").Start();
        root.SetTag(ConversationIdActivityProcessor.SessionAttributeName, "chat-123");
        using var child = new Activity("database query")
            .Start();
        child.Stop();

        new ConversationIdActivityProcessor().OnEnd(child);

        Assert.Null(child.GetTagItem(
            ConversationIdActivityProcessor.ConversationAttributeName));
    }

    [Fact]
    public void OnEnd_DoesNotOverwriteExistingConversationId()
    {
        using var root = new Activity("POST /api/chat").Start();
        root.SetTag(ConversationIdActivityProcessor.SessionAttributeName, "chat-123");
        using var child = new Activity("invoke_agent Coordinator")
            .Start();
        child.SetTag("gen_ai.operation.name", "invoke_agent");
        child.SetTag(
            ConversationIdActivityProcessor.ConversationAttributeName,
            "agent-chat-456");
        child.Stop();

        new ConversationIdActivityProcessor().OnEnd(child);

        Assert.Equal(
            "agent-chat-456",
            child.GetTagItem(
                ConversationIdActivityProcessor.ConversationAttributeName));
    }

    [Fact]
    public void OnEnd_CorrelatesModelAndToolSpansWithNearestAgent()
    {
        using var coordinator = new Activity("Coordinator").Start();
        coordinator.SetTag("gen_ai.agent.id", "Coordinator");
        coordinator.SetTag("gen_ai.agent.name", "Coordinator");
        using var specialist = new Activity("WeatherAdvisor").Start();
        specialist.SetTag("gen_ai.agent.id", "WeatherAdvisor");
        specialist.SetTag("gen_ai.agent.name", "WeatherAdvisor");
        specialist.SetTag("gen_ai.agent.description", "Weather specialist");
        using var model = new Activity("chat").Start();
        model.SetTag("gen_ai.operation.name", "chat");

        new ConversationIdActivityProcessor().OnEnd(model);

        Assert.Equal("WeatherAdvisor", model.GetTagItem("gen_ai.agent.id"));
        Assert.Equal("WeatherAdvisor", model.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("Weather specialist", model.GetTagItem("gen_ai.agent.description"));
    }

    [Fact]
    public void OnEnd_RemovesFailedToolResultAndPreservesError()
    {
        using var tool = new Activity("execute_tool").Start();
        tool.SetTag("gen_ai.operation.name", "execute_tool");
        tool.SetTag("gen_ai.tool.call.result", "null");
        tool.SetTag("error.type", "System.InvalidOperationException");
        tool.SetStatus(ActivityStatusCode.Error);

        new ConversationIdActivityProcessor().OnEnd(tool);

        Assert.Null(tool.GetTagItem("gen_ai.tool.call.result"));
        Assert.Equal("System.InvalidOperationException", tool.GetTagItem("error.type"));
    }
}
