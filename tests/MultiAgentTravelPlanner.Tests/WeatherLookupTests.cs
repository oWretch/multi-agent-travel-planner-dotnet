using System.Diagnostics;
using MultiAgentTravelPlanner.Tools;

namespace MultiAgentTravelPlanner.Tests;

public sealed class WeatherLookupTests
{
    [Theory]
    [InlineData("success", "succeeded", "succeeded", 3)]
    [InlineData("fail-once", "failed", "succeeded", 6)]
    [InlineData("persistent-failure", "failed", "failed", 6)]
    public async Task RecordsBoundedAttemptsAndOrderedTransitions(string mode, string firstState, string finalState, int eventCount)
    {
        using var capture = new TelemetryCapture(false);
        var lookup = new WeatherLookup(mode, TimeSpan.Zero);
        using var run = lookup.BeginRun();
        var first = await lookup.GetForecastAsync("Lisbon", "2026-11-12");
        var second = await lookup.GetForecastAsync("Lisbon", "2026-11-12");
        var third = await lookup.GetForecastAsync("Lisbon", "2026-11-12");

        Assert.Equal(firstState, first.State);
        Assert.Equal(finalState, second.State);
        Assert.Equal(second, third);
        Assert.False(third.Retryable);
        Assert.Equal(first.LookupId, second.LookupId);
        var transitions = capture.Activities.SelectMany(activity => activity.Events).ToArray();
        Assert.Equal(eventCount, transitions.Length);
        Assert.Equal(Enumerable.Range(1, eventCount), transitions.Select(transition =>
            (int)transition.Tags.Single(tag => tag.Key == "travel_planner.lookup.sequence").Value!));
        Assert.All(capture.Activities.Where(activity => activity.GetTagItem("error.type") is not null),
            activity => Assert.Equal(ActivityStatusCode.Error, activity.Status));
        Assert.DoesNotContain(transitions.SelectMany(transition => transition.Tags),
            tag => tag.Key.Contains("destination") || tag.Key.Contains("forecast"));
    }

    [Fact]
    public async Task BoundsAttemptsEvenWhenArgumentsChange()
    {
        var lookup = new WeatherLookup("persistent-failure", TimeSpan.Zero);
        using var run = lookup.BeginRun();
        await lookup.GetForecastAsync("Lisbon", "2026-11-12");
        await lookup.GetForecastAsync("Porto", "2026-11-13");
        var result = await lookup.GetForecastAsync("Paris", "2026-11-14");
        Assert.Equal("rejected", result.State);
        Assert.False(result.Retryable);
        Assert.Null(result.Forecast);
    }

    [Fact]
    public async Task ConcurrentRunsHaveIndependentState()
    {
        var lookup = new WeatherLookup("fail-once", TimeSpan.Zero);
        async Task<WeatherLookupResult[]> InvokeAsync()
        {
            using var run = lookup.BeginRun();
            await Task.Yield();
            return [await lookup.GetForecastAsync("Lisbon", "2026-11-12"),
                await lookup.GetForecastAsync("Lisbon", "2026-11-12")];
        }

        var results = await Task.WhenAll(InvokeAsync(), InvokeAsync());
        Assert.NotEqual(results[0][0].LookupId, results[1][0].LookupId);
        Assert.All(results, attempts =>
        {
            Assert.Equal("failed", attempts[0].State);
            Assert.Equal("succeeded", attempts[1].State);
        });
    }

    [Fact]
    public async Task ConcurrentCallsInOneRunSerializeStateAndRespectAttemptLimit()
    {
        using var capture = new TelemetryCapture(false);
        var lookup = new WeatherLookup("fail-once", TimeSpan.FromMilliseconds(5));
        using var run = lookup.BeginRun();
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => lookup.GetForecastAsync("Lisbon", "2026-11-12")));
        Assert.Single(results, result => result.State == "failed");
        Assert.Equal(3, results.Count(result => result.State == "succeeded"));
        Assert.Equal(2, capture.Activities.Count);
        Assert.Equal(Enumerable.Range(1, 6), capture.Activities.SelectMany(activity => activity.Events)
            .Select(spanEvent => (int)spanEvent.Tags.Single(tag => tag.Key == "travel_planner.lookup.sequence").Value!));
    }

    [Theory]
    [InlineData("unknown", 300, 30000)]
    [InlineData("success", -1, 30000)]
    [InlineData("success", 30001, 30000)]
    [InlineData("success", 300, 0)]
    [InlineData("success", 300, 60001)]
    public void InvalidHostConfigurationFailsFast(string mode, int delay, int timeout)
    {
        Assert.ThrowsAny<ArgumentException>(() => new WeatherLookup(mode,
            TimeSpan.FromMilliseconds(delay), TimeSpan.FromMilliseconds(timeout)));
    }

    [Fact]
    public async Task CancellationRecordsTerminalStateAndDoesNotBecomeSuccess()
    {
        using var capture = new TelemetryCapture(false);
        using var cancellation = new CancellationTokenSource();
        var lookup = new WeatherLookup(delay: TimeSpan.FromSeconds(10));
        using var run = lookup.BeginRun();
        var pending = lookup.GetForecastAsync("Lisbon", "2026-11-12", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var result = await lookup.GetForecastAsync("Lisbon", "2026-11-12");
        Assert.Equal("cancelled", result.State);
        Assert.Null(result.Forecast);
        Assert.Equal("cancelled", Assert.Single(capture.Activities).GetTagItem("travel_planner.lookup.state"));
    }

    [Fact]
    public async Task TimeoutIsTerminalAndDoesNotBecomeSuccess()
    {
        using var capture = new TelemetryCapture(false);
        var lookup = new WeatherLookup(delay: TimeSpan.FromSeconds(10), timeout: TimeSpan.FromMilliseconds(50));
        using var run = lookup.BeginRun();
        var first = await lookup.GetForecastAsync("Lisbon", "2026-11-12");
        var second = await lookup.GetForecastAsync("Lisbon", "2026-11-12");
        Assert.Equal("timed_out", first.State);
        Assert.Equal(first, second);
        Assert.Equal("timed_out", Assert.Single(capture.Activities).GetTagItem("travel_planner.lookup.state"));
    }
}