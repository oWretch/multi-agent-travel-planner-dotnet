using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MultiAgentTravelPlanner.Observability;

// Runs inside OpenTelemetryChatClient so enrichment belongs to the existing inference span.
public sealed class GenAiTelemetryChatClient(
    IChatClient innerClient,
    bool useResponsesApi) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var activity = ConfigureRequest(options, streaming: false);
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        RecordUsage(activity, response.Usage);
        RecordResponseStatus(activity, response.RawRepresentation);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var activity = ConfigureRequest(options, streaming: true);
        var started = Stopwatch.GetTimestamp();
        var firstChunk = true;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            if (firstChunk)
            {
                activity?.SetTag(
                    "gen_ai.response.time_to_first_chunk",
                    Stopwatch.GetElapsedTime(started).TotalSeconds);
                firstChunk = false;
            }

            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                RecordUsage(activity, usage.Details);
            }

            RecordResponseStatus(activity, update.RawRepresentation);
            yield return update;
        }
    }

    private Activity? ConfigureRequest(ChatOptions? options, bool streaming)
    {
        var activity = Activity.Current;
        if (activity is not { IsAllDataRequested: true }
            || activity.GetTagItem("gen_ai.operation.name") is not "chat")
        {
            return null;
        }

        activity.SetTag("gen_ai.request.stream", streaming);
        if (options?.Reasoning?.Effort is { } effort)
        {
            activity.SetTag("gen_ai.request.reasoning.level", effort switch
            {
                ReasoningEffort.None => "none",
                ReasoningEffort.Low => "low",
                ReasoningEffort.Medium => "medium",
                ReasoningEffort.High => "high",
                ReasoningEffort.ExtraHigh => "xhigh",
                _ => throw new ArgumentOutOfRangeException(nameof(options), effort, "Unsupported reasoning effort."),
            });
        }

        // The Responses adapter uses ConversationId for either a hosted conversation or a previous response.
        if (useResponsesApi && options?.ConversationId is { } id && !id.StartsWith("conv_", StringComparison.Ordinal))
        {
            activity.SetTag("gen_ai.request.previous_response.id", id);
            activity.SetTag("gen_ai.conversation.id", null);
        }

        if (options?.StopSequences is { Count: > 0 } stops)
        {
            activity.SetTag("gen_ai.request.stop_sequences", stops.ToArray());
        }

        return activity;
    }

    private static void RecordUsage(Activity? activity, UsageDetails? usage)
    {
        if (activity is null || usage is null)
        {
            return;
        }

        SetCount("gen_ai.usage.reasoning.output_tokens", usage.ReasoningTokenCount);
#pragma warning disable MEAI001
        SetCount("gen_ai.usage.text.input_tokens", usage.InputTextTokenCount);
        SetCount("gen_ai.usage.text.output_tokens", usage.OutputTextTokenCount);
        SetCount("gen_ai.usage.audio.input_tokens", usage.InputAudioTokenCount);
        SetCount("gen_ai.usage.audio.output_tokens", usage.OutputAudioTokenCount);
#pragma warning restore MEAI001

        void SetCount(string name, long? count)
        {
            if (count is { } value)
            {
                activity.SetTag(name, value);
            }
        }
    }

#pragma warning disable OPENAI001
    private static void RecordResponseStatus(Activity? activity, object? rawResponse)
    {
        var response = rawResponse switch
        {
            ResponseResult result => result,
            StreamingResponseCreatedUpdate created => created.Response,
            StreamingResponseInProgressUpdate inProgress => inProgress.Response,
            StreamingResponseQueuedUpdate queued => queued.Response,
            StreamingResponseCompletedUpdate completed => completed.Response,
            StreamingResponseIncompleteUpdate incomplete => incomplete.Response,
            StreamingResponseFailedUpdate failed => failed.Response,
            _ => null,
        };

        if (response?.Status is { } status)
        {
            activity?.SetTag("gen_ai.response.status", status == ResponseStatus.InProgress
                ? "in_progress"
                : status.ToString().ToLowerInvariant());

            if (status == ResponseStatus.Failed)
            {
                throw new InvalidOperationException(
                    $"OpenAI response {response.Id} failed: {response.Error?.Message ?? "No error details returned."}");
            }

            if (status == ResponseStatus.Cancelled)
            {
                throw new OperationCanceledException($"OpenAI response {response.Id} was cancelled.");
            }
        }
    }
#pragma warning restore OPENAI001
}
