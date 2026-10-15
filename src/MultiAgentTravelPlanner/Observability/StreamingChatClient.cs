using Microsoft.Extensions.AI;

namespace MultiAgentTravelPlanner.Observability;

// Buffers model streaming without changing the agent or HTTP response contract.
public sealed class StreamingChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        InnerClient.GetStreamingResponseAsync(messages, options, cancellationToken)
            .ToChatResponseAsync(cancellationToken);
}
