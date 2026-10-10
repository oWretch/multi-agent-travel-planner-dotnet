# Observability learnings

This sample exposes a few differences between OpenTelemetry trace structure,
Microsoft Foundry's trace UI, and Azure billing data. These notes record what
the application emits today, what Microsoft documents, and where the platform
behavior is still an implementation detail.

## Current trace shape

ASP.NET Core instrumentation creates the root span for each request:

```text
POST /api/chat
└── invoke_agent Coordinator
    ├── execute_tool WeatherAdvisor
    │   └── invoke_agent WeatherAdvisor
    ├── execute_tool CurrencyConverter
    │   └── invoke_agent CurrencyConverter
    └── ...
```

Keeping the HTTP span is useful. It records request latency, status codes, and
failures that happen before an agent runs. It also keeps the complete request
waterfall in one trace.

## What Application Insights contains

The following observations were verified against the deployed Application
Insights resource on 10 October 2026.

For a successful fixed-model request:

- `POST /api/chat` was the root span.
- The coordinator and each specialist emitted an `invoke_agent` dependency
  span.
- Every completed `invoke_agent` span contained
  `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`,
  `gen_ai.request.model`, and `gen_ai.response.model`.
- The model-router traces correctly distinguished the requested deployment
  (`model-router`) from the selected response model.
- The HTTP root contained none of those `gen_ai.*` usage or model attributes.
- No span contained `gen_ai.usage.cost` or `gen_ai.usage.total_cost`.

One trace included these agent values:

| Agent | Request model | Input tokens | Output tokens |
|---|---|---:|---:|
| Coordinator | `gpt-6-astra` | 8,708 | 2,134 |
| WeatherAdvisor | `gpt-5.4-mini` | 394 | 571 |
| CurrencyConverter | `gpt-5.4-mini` | 344 | 470 |
| BudgetOptimizer | `gpt-5.6-luna` | 436 | 1,337 |
| LocalKnowledge | `gpt-5.4-mini` | 312 | 221 |
| ItineraryPlanner | `gpt-5.6-luna` | 303 | 3,591 |

This confirms that missing trace cost is not caused by missing token or model
attributes on the agent spans. The instrumentation emits those values, but
Foundry does not add a monetary cost to these custom traces.

The verification query was:

```kusto
union withsource=TableName requests, dependencies, traces
| where operation_Id == "<operation-id>"
| extend
    input_tokens = tostring(customDimensions["gen_ai.usage.input_tokens"]),
    output_tokens = tostring(customDimensions["gen_ai.usage.output_tokens"]),
    request_model = tostring(customDimensions["gen_ai.request.model"]),
    response_model = tostring(customDimensions["gen_ai.response.model"]),
    usage_cost = tostring(customDimensions["gen_ai.usage.cost"]),
    total_cost = tostring(customDimensions["gen_ai.usage.total_cost"])
| project
    timestamp,
    TableName,
    name,
    id,
    operation_ParentId,
    input_tokens,
    output_tokens,
    request_model,
    response_model,
    usage_cost,
    total_cost
| order by timestamp asc
```

## What Microsoft documents about cost

Microsoft's Foundry tracing documentation says traces capture token usage and
use the OpenTelemetry generative AI semantic conventions. It does not document
a contract where Foundry calculates trace-level currency amounts from custom
`invoke_agent` spans.

The Foundry cost guidance treats billing as a separate concern:

- Prices vary by model, deployment type, region, and meter.
- Foundry Monitor can show token and estimated-cost charts for supported model
  deployments.
- Azure Cost Management and the invoice are the source of truth for billed
  cost.

This distinction matters for the sample. A trace is application telemetry.
Azure billing is produced from service meters. Matching the two requires model,
deployment, region, pricing mode, cached-token treatment, and the price
effective at the time of the request. Token counts alone are not enough.

For presentation purposes, the safe statement is:

> The agent framework emits model and token usage on `invoke_agent` spans, but
> Foundry does not currently show a monetary cost for these custom traces.
> Use Foundry Monitor or Azure Cost Management for service cost, or calculate a
> clearly labelled estimate if per-trace cost is required.

If the application calculates an estimate, retain the actual
`gen_ai.response.model` for each model call. Do not assign one model to the
`POST /api/chat` root when a request uses several models. A root-level estimate
should be the sum of the model-specific child estimates and should not be
presented as billed cost.

## Root token totals

Copying aggregate input and output tokens to `POST /api/chat` may make a
trace-summary UI more useful, but this is an application-level rollup. Standard
OpenTelemetry processors do not copy child attributes to their parent.

The root can safely carry:

```text
gen_ai.usage.input_tokens
gen_ai.usage.output_tokens
```

It should carry `gen_ai.request.model` or `gen_ai.response.model` only when the
whole request used one model. That is not true for the fixed multi-model
strategy and might not be true for a model-router request.

Microsoft documents root-span token fields for its Azure Databricks MLflow
tracing integration, but the current Foundry tracing documentation does not
state that its trace summary has the same root-span requirement. Treat the
observed Foundry UI behavior as a platform quirk rather than a
semantic-convention rule.

## Conversation identifiers

The application has an application-level conversation identifier. Agent
Framework does not currently bind that identifier to
`gen_ai.conversation.id` automatically in this sample's .NET package version,
so the application adds the attribute explicitly.

`ChatRequest.SessionId` is created or accepted by `POST /api/chat`, returned to
the browser, and used as the key for `ConversationStore`. The store maps that
value to the coordinator's `AgentSession`. The endpoint sets it as `session.id`
on the HTTP activity. `ConversationIdActivityProcessor` then sets
`gen_ai.conversation.id` only on descendant spans that have a
`gen_ai.operation.name`, including agent and model spans.

Before this change, Application Insights was checked on 10 October 2026. No
request, dependency, or trace record from the previous 48 hours contained
either:

```text
gen_ai.conversation.id
session.id
```

The current behavior therefore provides trace correlation through
`operation_Id`, but not conversation correlation across multiple requests.
Each request has a new trace ID even when the browser sends the same
`SessionId`.

These identifiers describe related but different concepts:

| Value | Purpose |
|---|---|
| W3C `traceparent` | Connects spans for one distributed operation |
| `gen_ai.conversation.id` | Connects AI operations that belong to one logical conversation |
| `session.id` | Identifies an application or user session for general telemetry |
| `AgentSession` | Holds or references an individual agent's conversation state |

For this sample, the browser-facing `SessionId` is used as `session.id` on the
HTTP request and as `gen_ai.conversation.id` on generative AI spans because it
is stable across chat turns. IDs generated by the server are opaque GUIDs, but
caller-supplied values are not currently constrained. The API should validate
their format and length before writing them to telemetry. Using the same value
for both attributes is an application decision; the semantic conventions do
not make the attributes equivalent.

The specialist calls do not receive the coordinator's `AgentSession`.
`AsAIFunction()` is called without a specialist session, so the Agent Framework
can create a new specialist session for each function invocation. This is
separate from telemetry correlation. Several agents can share one
`gen_ai.conversation.id` while keeping independent provider threads and state.

Microsoft's direct OpenTelemetry guidance for agent-to-agent calls says to
reuse the same `gen_ai.conversation.id` across the calling and target agents.
It also recommends recording caller-agent attributes on the target span.

### Propagation across HTTP endpoints

`gen_ai.conversation.id` is a span attribute, not part of W3C Trace Context.
The standard `traceparent` and `tracestate` headers do not carry it, and span
attributes are not inherited automatically.

A remote-agent contract should therefore carry the conversation ID explicitly.
For example:

```json
{
  "conversationId": "opaque-application-conversation-id",
  "message": "Plan activities for Saturday"
}
```

The receiving endpoint should validate the value, use it to find or create the
target agent's own state, and set `gen_ai.conversation.id` on the target
`invoke_agent` span and its relevant child spans.

W3C baggage can also carry an opaque conversation ID, but baggage does not
automatically become a span attribute. Code on the receiver must still read it
and set the attribute. Baggage is propagated to downstream services, so it
must not contain prompts, personal data, credentials, or provider session
tokens.

An explicit message field is easier to reason about for this sample:

- The API contract makes the dependency visible.
- Validation and authorization happen with the rest of the request.
- It works whether the agents share a trace, use linked traces, or run through
  a queue instead of HTTP.

If agent traces are split and linked, `gen_ai.conversation.id` becomes the
stable query key across those trace IDs. Trace links explain causality for one
invocation, while the conversation ID groups all turns and participating
agents.

The query used to check the deployed telemetry was:

```kusto
union withsource=TableName requests, dependencies, traces
| where timestamp > ago(48h)
| extend
    conversation_id =
        tostring(customDimensions["gen_ai.conversation.id"]),
    session_id = tostring(customDimensions["session.id"])
| where isnotempty(conversation_id) or isnotempty(session_id)
| project
    timestamp,
    TableName,
    name,
    operation_Id,
    conversation_id,
    session_id
| order by timestamp desc
```

## Parent context and span links

An OpenTelemetry span has one parent. Sending the standard W3C `traceparent`
and `tracestate` headers makes the receiver a descendant in the same trace. It
does not create a separate linked trace.

To create an agent trace that is separate from the HTTP transport trace:

1. Capture the current transport `ActivityContext`.
2. Start the agent activity with no parent, which gives it a new trace ID.
3. Add an `ActivityLink` to the captured transport context when the agent
   activity starts.

In .NET, the essential shape is:

```csharp
var transportContext = Activity.Current?.Context;
var links = transportContext is { } context
    ? new[] { new ActivityLink(context) }
    : null;

using var agentActivity = AgentActivitySource.StartActivity(
    "invoke_agent Coordinator",
    ActivityKind.Internal,
    parentContext: default,
    tags: null,
    links: links);
```

Links are immutable after an activity starts, so the contexts must be available
at creation time.

## Passing transport and agent context to a remote agent

There cannot be separate transport and agent parents on one remote span. Choose
one parent and represent the other causal relationship with a link.

If a remote call only propagates the standard OpenTelemetry headers, it carries
one context. The receiving HTTP span and agent span will normally join that
same trace.

To keep transport traces separate while giving each remote agent its own root
trace, the message or API must carry enough information to create links:

```text
Transport trace T1
POST /api/chat
└── outbound HTTP call
    └── remote HTTP server span

Agent trace T2
invoke_agent Coordinator [link to T1]

Agent trace T3
invoke_agent Specialist [links to its transport span and T2]
```

A practical remote-call contract is:

- Use normal `traceparent` and `tracestate` for the transport parent.
- Carry the upstream agent context in separate message metadata, such as a
  dedicated `agent-traceparent` field.
- On the receiver, let HTTP instrumentation create the transport span.
- Start a new root agent activity and add links to the local transport context
  and the parsed upstream agent context.

Do not place a second parent in `tracestate`. `tracestate` provides
vendor-specific data for the single context identified by `traceparent`; it is
not a container for another parent.

This does require an API or message-contract addition when the current
transport carries only the standard OpenTelemetry headers. The alternative is
to propagate the agent context as the standard parent, which preserves one
distributed agent trace but no longer gives each agent a separate root trace.

For the current in-process `AsAIFunction()` calls, no transport contract exists.
Creating separate linked traces would require a wrapper or framework extension
that starts the agent activity manually with the required links.

## Recommended approach for this demo

Keep the current hierarchy unless per-agent trace-summary cost becomes a firm
requirement. It gives the best debugging experience and requires no custom
context protocol.

Use these reporting layers:

| Question | Source |
|---|---|
| Did the web request succeed? | `POST /api/chat` span |
| Which agent or tool was slow? | `invoke_agent` and `execute_tool` spans |
| How many tokens did an agent use? | `gen_ai.usage.*` on `invoke_agent` |
| Which model was selected? | `gen_ai.response.model` |
| What did the Azure service cost? | Foundry Monitor or Azure Cost Management |
| What was the estimated cost of this trace? | Application-owned calculation, if implemented |

Separate linked traces are reasonable when agents become independently
deployed services and per-agent trace summaries matter more than one waterfall.
At that point, add an explicit agent-link context to the remote-call contract
rather than trying to encode two parents in the standard OpenTelemetry headers.

## References

- [Agent tracing overview](https://learn.microsoft.com/azure/foundry/observability/concepts/trace-agent-concept)
- [Tracing and data handling](https://learn.microsoft.com/azure/foundry/observability/concepts/trace-data)
- [Set up tracing in Microsoft Foundry](https://learn.microsoft.com/azure/foundry/observability/how-to/trace-agent-setup)
- [Plan and manage costs for Microsoft Foundry](https://learn.microsoft.com/azure/foundry/concepts/manage-costs)
- [Azure Databricks MLflow manual tracing](https://learn.microsoft.com/azure/databricks/mlflow3/genai/tracing/manual-tracing#custom-opentelemetry-instrumentation)
- [Agent 365 direct OpenTelemetry integration](https://learn.microsoft.com/microsoft-agent-365/developer/direct-open-telemetry-integration#message-contracts)
- [Adding distributed tracing instrumentation: links](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs#optional-links)
- [ActivityLink class](https://learn.microsoft.com/dotnet/api/system.diagnostics.activitylink)
- [W3C Trace Context](https://www.w3.org/TR/trace-context/)
