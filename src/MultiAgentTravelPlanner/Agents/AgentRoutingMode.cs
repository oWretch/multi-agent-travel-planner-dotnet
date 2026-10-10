namespace MultiAgentTravelPlanner.Agents;

public enum AgentRoutingMode
{
    Fixed,
    ModelRouter,
}

public static class AgentRoutingModeParser
{
    public static bool TryParse(string? value, out AgentRoutingMode mode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("fixed", StringComparison.OrdinalIgnoreCase))
        {
            mode = AgentRoutingMode.Fixed;
            return true;
        }

        if (value.Equals("model-router", StringComparison.OrdinalIgnoreCase))
        {
            mode = AgentRoutingMode.ModelRouter;
            return true;
        }

        mode = default;
        return false;
    }

    public static string ToConfigurationValue(this AgentRoutingMode mode) => mode switch
    {
        AgentRoutingMode.Fixed => "fixed",
        AgentRoutingMode.ModelRouter => "model-router",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
