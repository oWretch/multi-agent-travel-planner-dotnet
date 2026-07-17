using Microsoft.Extensions.Configuration;
using MultiAgentTravelPlanner.Agents;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentIdConfigurationTests
{
    [Fact]
    public void Resolve_UsesLogicalNameByDefault()
    {
        var configuration = BuildConfiguration();

        Assert.Equal(
            "WeatherAdvisor",
            AgentIdConfiguration.Resolve(configuration, "WeatherAdvisor"));
    }

    [Fact]
    public void Resolve_UsesConfiguredId()
    {
        var configuration = BuildConfiguration(
            ("Agents:WeatherAdvisor:Id", "weather-v2"));

        Assert.Equal(
            "weather-v2",
            AgentIdConfiguration.Resolve(configuration, "WeatherAdvisor"));
    }

    [Fact]
    public void Resolve_RejectsEmptyConfiguredId()
    {
        var configuration = BuildConfiguration(
            ("Agents:WeatherAdvisor:Id", " "));

        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentIdConfiguration.Resolve(configuration, "WeatherAdvisor"));

        Assert.Contains(
            "Agents:WeatherAdvisor:Id must not be empty",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateDistinct_RejectsDuplicateIds()
    {
        var configuration = BuildConfiguration(
            ("Agents:WeatherAdvisor:Id", "shared-id"),
            ("Agents:CurrencyConverter:Id", "shared-id"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => AgentIdConfiguration.ValidateDistinct(
                configuration,
                ["WeatherAdvisor", "CurrencyConverter"]));

        Assert.Contains(
            "Agent IDs must be unique",
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
