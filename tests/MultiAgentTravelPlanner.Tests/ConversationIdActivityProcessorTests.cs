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
}
