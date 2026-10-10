using Microsoft.Agents.AI;

namespace MultiAgentTravelPlanner.Agents;

public sealed class AgentCatalogRegistry
{
    public AgentCatalogRegistry(AgentCatalog fixedCatalog, AgentCatalog modelRouterCatalog)
    {
        Fixed = fixedCatalog;
        ModelRouter = modelRouterCatalog;
    }

    public AgentCatalog Fixed { get; }
    public AgentCatalog ModelRouter { get; }

    public AgentCatalog Resolve(AgentRoutingMode mode) => mode switch
    {
        AgentRoutingMode.Fixed => Fixed,
        AgentRoutingMode.ModelRouter => ModelRouter,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public AIAgent ResolveCoordinator(AgentRoutingMode mode) => Resolve(mode).Coordinator;
}
