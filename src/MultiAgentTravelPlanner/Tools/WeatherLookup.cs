using System.Diagnostics;
using MultiAgentTravelPlanner.Agents;
using MultiAgentTravelPlanner.Observability;

namespace MultiAgentTravelPlanner.Tools;

public sealed class WeatherLookup
{
    public const string TransitionEventName = "travel_planner.lookup.state_changed";
    public const int MaxAttempts = 2;
    private static readonly ActivitySource Source = new(AgentCatalog.TelemetrySourceName);
    private readonly AsyncLocal<Run?> currentRun = new();
    private readonly string mode;
    private readonly TimeSpan delay;
    private readonly TimeSpan timeout;
    private readonly TimeProvider clock;

    public WeatherLookup(
        string mode = "success",
        TimeSpan? delay = null,
        TimeSpan? timeout = null,
        TimeProvider? clock = null)
    {
        if (mode is not ("success" or "fail-once" or "persistent-failure"))
        {
            throw new ArgumentException("Weather mode must be success, fail-once, or persistent-failure.", nameof(mode));
        }

        this.mode = mode;
        this.delay = delay ?? TimeSpan.FromMilliseconds(300);
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);
        this.clock = clock ?? TimeProvider.System;
        if (this.delay < TimeSpan.Zero || this.delay > TimeSpan.FromSeconds(30)
            || this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Use a 0-30 second delay and a positive timeout of at most 60 seconds.");
        }
    }

    public IDisposable BeginRun()
    {
        var previous = currentRun.Value;
        var run = new Run(timeout, clock);
        currentRun.Value = run;
        Activity.Current?.SetTag("travel_planner.lookup.mode", mode);
        Activity.Current?.SetTag("travel_planner.lookup.max_attempts", MaxAttempts);
        Activity.Current?.SetTag("travel_planner.lookup.timeout_ms", timeout.TotalMilliseconds);
        Activity.Current?.SetTag("travel_planner.lookup.delay_ms", delay.TotalMilliseconds);
        return new RunScope(() =>
        {
            currentRun.Value = previous;
            run.Dispose();
        });
    }

    public async Task<WeatherLookupResult> GetForecastAsync(
        string destination, string date, CancellationToken cancellationToken = default)
    {
        var run = currentRun.Value ?? throw new InvalidOperationException("A weather lookup requires an active agent run.");
        await run.Gate.WaitAsync(cancellationToken);
        try
        {
            var key = (destination, date);
            if (run.Results.TryGetValue(key, out var previous) && !previous.Retryable)
            {
                return previous;
            }

            if (run.Attempts >= MaxAttempts || run.Budget.IsCancellationRequested)
            {
                return new(run.Id, run.Attempts, "rejected", false, "run_limit_reached", null);
            }

            var attempt = ++run.Attempts;
            using var activity = Source.StartActivity("weather_lookup", ActivityKind.Internal);
            activity?.SetTag("travel_planner.lookup.id", run.Id);
            activity?.SetTag("travel_planner.lookup.attempt", attempt);
            activity?.SetTag("travel_planner.lookup.mode", mode);
            activity?.SetTag("travel_planner.tools.data.kind", "synthetic");
            string? state = null;
            Transition("pending", "accepted");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Budget.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                Transition("running", "lookup_started");
                await Task.Delay(delay, clock, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (mode == "persistent-failure" || (mode == "fail-once" && attempt == 1))
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                    activity?.SetTag("error.type", "synthetic_weather_unavailable");
                    Transition("failed", "synthetic_weather_unavailable");
                    return Save(new(run.Id, attempt, "failed", attempt < MaxAttempts, "synthetic_weather_unavailable", null));
                }

                Transition("succeeded", "forecast_available");
                return Save(new(run.Id, attempt, "succeeded", false, "forecast_available",
                    $"Forecast for {destination} on {date}: 24 C, partly cloudy, light breeze, 10% chance of rain."));
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                var cancelled = cancellationToken.IsCancellationRequested;
                var terminal = cancelled ? "cancelled" : "timed_out";
                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.SetTag("error.type", terminal);
                Transition(terminal, cancelled ? "caller_cancelled" : "run_deadline_exceeded");
                var result = Save(new(run.Id, attempt, terminal, false, terminal, null));
                if (cancelled)
                {
                    throw;
                }
                return result;
            }

            WeatherLookupResult Save(WeatherLookupResult result)
            {
                run.Results[key] = result;
                return result;
            }

            void Transition(string next, string reason)
            {
                var tags = new ActivityTagsCollection
                {
                    ["travel_planner.lookup.id"] = run.Id,
                    ["travel_planner.lookup.attempt"] = attempt,
                    ["travel_planner.lookup.sequence"] = ++run.Sequence,
                    ["travel_planner.lookup.previous_state"] = state ?? "none",
                    ["travel_planner.lookup.state"] = next,
                    ["travel_planner.lookup.reason"] = reason,
                    ["travel_planner.tools.data.kind"] = "synthetic",
                };
                activity?.SetTag("travel_planner.lookup.state", next);
                activity?.AddEvent(new ActivityEvent(TransitionEventName, clock.GetUtcNow(),
                    ConversationIdActivityProcessor.CorrelateEvent(activity, tags)));
                state = next;
            }
        }
        finally
        {
            run.Gate.Release();
        }
    }

    private sealed class Run(TimeSpan timeout, TimeProvider clock) : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SemaphoreSlim Gate { get; } = new(1);
        private CancellationTokenSource? budget;
        public CancellationTokenSource Budget => budget ??= new(timeout, clock);
        public Dictionary<(string Destination, string Date), WeatherLookupResult> Results { get; } = [];
        public int Attempts { get; set; }
        public int Sequence { get; set; }

        public void Dispose()
        {
            budget?.Dispose();
            Gate.Dispose();
        }
    }

    private sealed class RunScope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed record WeatherLookupResult(
    string LookupId, int Attempt, string State, bool Retryable, string Reason, string? Forecast);