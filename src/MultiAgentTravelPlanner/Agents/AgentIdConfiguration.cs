namespace MultiAgentTravelPlanner.Agents;

public static class AgentIdConfiguration
{
    public static string Resolve(
        IConfiguration configuration,
        string logicalName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalName);

        var key = $"Agents:{logicalName}:Id";
        var configuredId = configuration[key];

        if (configuredId is null)
        {
            return logicalName;
        }

        if (string.IsNullOrWhiteSpace(configuredId))
        {
            throw new InvalidOperationException($"{key} must not be empty.");
        }

        return configuredId;
    }

    public static void ValidateDistinct(
        IConfiguration configuration,
        IEnumerable<string> logicalNames)
    {
        ArgumentNullException.ThrowIfNull(logicalNames);

        var duplicates = logicalNames
            .Select(logicalName => new
            {
                LogicalName = logicalName,
                Id = Resolve(configuration, logicalName),
            })
            .GroupBy(agent => agent.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"{group.Key} ({string.Join(", ", group.Select(agent => agent.LogicalName))})")
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Agent IDs must be unique. Duplicate values: {string.Join("; ", duplicates)}.");
        }
    }
}
