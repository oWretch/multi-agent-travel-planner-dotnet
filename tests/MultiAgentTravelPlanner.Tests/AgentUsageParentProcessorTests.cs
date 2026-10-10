using System.Diagnostics;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tests;

public sealed class AgentUsageParentProcessorTests
{
    [Fact]
    public void OnEnd_CopiesCoordinatorTokenUsageToRequestActivity()
    {
        using var listener = CreateListener();
        using var source = new ActivitySource(nameof(
            OnEnd_CopiesCoordinatorTokenUsageToRequestActivity));
        using var request = source.StartActivity(
            "POST /api/chat",
            ActivityKind.Server);
        using var coordinator = source.StartActivity(
            "invoke_agent Coordinator",
            ActivityKind.Internal);

        Assert.NotNull(request);
        Assert.NotNull(coordinator);

        coordinator.SetTag("gen_ai.operation.name", "invoke_agent");
        coordinator.SetTag("gen_ai.agent.name", "Coordinator");
        coordinator.SetTag("gen_ai.usage.input_tokens", 120L);
        coordinator.SetTag("gen_ai.usage.output_tokens", 45L);
        coordinator.SetTag("gen_ai.usage.cache_read.input_tokens", 20L);
        coordinator.SetTag("gen_ai.request.model", "gpt-test");

        new AgentUsageParentProcessor().OnEnd(coordinator);

        Assert.Equal(120L, request.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(45L, request.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(
            20L,
            request.GetTagItem("gen_ai.usage.cache_read.input_tokens"));
        Assert.Null(request.GetTagItem("gen_ai.request.model"));
    }

    [Fact]
    public void OnEnd_DoesNotCopySpecialistTokenUsageToRequestActivity()
    {
        using var listener = CreateListener();
        using var source = new ActivitySource(nameof(
            OnEnd_DoesNotCopySpecialistTokenUsageToRequestActivity));
        using var request = source.StartActivity(
            "POST /api/chat",
            ActivityKind.Server);
        using var coordinator = source.StartActivity(
            "invoke_agent Coordinator",
            ActivityKind.Internal);
        using var specialist = source.StartActivity(
            "invoke_agent WeatherAdvisor",
            ActivityKind.Internal);

        Assert.NotNull(request);
        Assert.NotNull(coordinator);
        Assert.NotNull(specialist);

        specialist.SetTag("gen_ai.operation.name", "invoke_agent");
        specialist.SetTag("gen_ai.agent.name", "WeatherAdvisor");
        specialist.SetTag("gen_ai.usage.input_tokens", 80L);
        specialist.SetTag("gen_ai.usage.output_tokens", 30L);

        new AgentUsageParentProcessor().OnEnd(specialist);

        Assert.Null(request.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Null(request.GetTagItem("gen_ai.usage.output_tokens"));
    }

    private static ActivityListener CreateListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
