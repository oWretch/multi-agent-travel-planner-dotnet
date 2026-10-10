using Microsoft.Extensions.Configuration;
using MultiAgentTravelPlanner.Agents;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentDeploymentConfigurationTests
{
    [Fact]
    public void Resolve_UsesDefaultDeploymentWhenAgentOverrideIsMissing()
    {
        var configuration = BuildConfiguration();

        Assert.Equal(
            "default-model",
            AgentDeploymentConfiguration.Resolve(
                configuration,
                "WeatherAdvisor",
                "default-model"));
    }

    [Fact]
    public void Resolve_UsesAgentDeploymentOverride()
    {
        var configuration = BuildConfiguration(
            ("Agents:WeatherAdvisor:Deployment", "fast-model"));

        Assert.Equal(
            "fast-model",
            AgentDeploymentConfiguration.Resolve(
                configuration,
                "WeatherAdvisor",
                "default-model"));
    }

    [Fact]
    public void Resolve_RejectsEmptyAgentDeploymentOverride()
    {
        var configuration = BuildConfiguration(
            ("Agents:WeatherAdvisor:Deployment", " "));

        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentDeploymentConfiguration.Resolve(
                configuration,
                "WeatherAdvisor",
                "default-model"));

        Assert.Contains(
            "Agents:WeatherAdvisor:Deployment must not be empty",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(
        params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(
                item => item.Key,
                item => item.Value))
            .Build();
}
