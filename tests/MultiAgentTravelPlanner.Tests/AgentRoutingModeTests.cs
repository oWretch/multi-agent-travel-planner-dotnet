using MultiAgentTravelPlanner.Agents;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentRoutingModeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fixed")]
    [InlineData("FIXED")]
    public void TryParse_UsesFixedModeByDefault(string? value)
    {
        Assert.True(AgentRoutingModeParser.TryParse(value, out var mode));
        Assert.Equal(AgentRoutingMode.Fixed, mode);
    }

    [Theory]
    [InlineData("model-router")]
    [InlineData("MODEL-ROUTER")]
    public void TryParse_UsesModelRouterMode(string value)
    {
        Assert.True(AgentRoutingModeParser.TryParse(value, out var mode));
        Assert.Equal(AgentRoutingMode.ModelRouter, mode);
    }

    [Fact]
    public void TryParse_RejectsUnknownMode()
    {
        Assert.False(AgentRoutingModeParser.TryParse("unknown", out _));
    }
}
