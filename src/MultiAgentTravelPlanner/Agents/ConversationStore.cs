using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace MultiAgentTravelPlanner.Agents;

/// <summary>
/// In-memory store for per-session <see cref="AgentSession"/> objects. This is
/// intentionally simple. Production apps should externalize state.
/// </summary>
public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<(string SessionId, AgentRoutingMode Mode), AgentSession> _sessions = new();

    public async ValueTask<AgentSession> GetOrCreateAsync(
        string sessionId,
        AgentRoutingMode mode,
        AIAgent agent,
        CancellationToken cancellationToken = default)
    {
        var key = (sessionId, mode);
        if (_sessions.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var created = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        return _sessions.GetOrAdd(key, created);
    }
}
