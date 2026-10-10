namespace MultiAgentTravelPlanner.Agents;

public static class AgentDeploymentConfiguration
{
    public static string Resolve(
        IConfiguration configuration,
        string logicalName,
        string defaultDeployment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultDeployment);

        var key = $"Agents:{logicalName}:Deployment";
        var configuredDeployment = configuration[key];

        if (configuredDeployment is null)
        {
            return defaultDeployment;
        }

        if (string.IsNullOrWhiteSpace(configuredDeployment))
        {
            throw new InvalidOperationException($"{key} must not be empty.");
        }

        return configuredDeployment;
    }
}
