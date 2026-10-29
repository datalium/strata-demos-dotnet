# 23 — agent-tool-loop

The agent from `design/44-LLM-Integration.md` ("The agent's loop", "Tool
names", "Structured output"): `StrataAgent<TOutput>` and `AddAgentNode` in
`Datalium.Strata.AI`. A support desk's refund agent looks an order up, issues
a refund through a `ToolRegistry` of `[StrataTool]` methods, and answers with
a typed `RefundDecision` that is parsed, validated and repaired. The demo then
breaks the loop on purpose: bad tool calls, a failing tool, a bad answer, and
a model that never stops calling tools.

**The model is a stand-in.** `ScriptedModel` is a small `IChatClient` that
plays a script, one turn per request, and records what it was sent. A turn is
a function of the conversation, so it can read the tool results (an order's
total, a refund id) the way a model would, but there is no intelligence in it:
the script decides everything. That keeps the demo offline, with no API key,
and makes every run identical. The agent only sees `IChatClient`, so it can't
tell the difference. [`03-llm-tool-calling`](../03-llm-tool-calling) runs the
same kind of loop against a real OpenAI model.

The Rust twin is
[`strata-demos-rust/demos/23-agent-tool-loop`](https://github.com/datalium/strata-demos-rust/tree/main/demos/23-agent-tool-loop);
its README says where the two engines differ.

## What it demonstrates

- **The loop** (scenario 1). Each turn is one model call with the registry's
  tools declared. The agent dispatches the answer's tool calls through the
  registry, sends the results back in **one tool message, in the model's
  order** (turn 1 has two calls, so its tool message has two results), and
  goes round again. An answer with no tool calls ends the loop.
- **`AgentResult<TOutput>`**: `Output`, `Turns`, every tool call as
  `AgentToolCall(Name, CallId, Result)`, the whole conversation in `Messages`,
  and `Usage` summed over the turns (checked against what the stand-in
  reported).
- **Provider-safe tool names.** `orders.lookup` is declared as
  `orders_lookup` (`ToolDeclarations.For(registry)`, which the agent builds
  once). The model calls `orders_lookup`; the agent maps it back, so the
  registry, its `ToolCallStarted` events and `AgentToolCall.Name` all see
  `orders.lookup`, with the agent's id as `CallerId` and the model's id as
  `CallId`.
- **A registry `Subset`.** The desk has four tools; the agent gets three.
  `accounts.close` is not declared to the model, and a call to it is refused.
- **A typed answer** (scenario 2). `StrataAgent<RefundDecision>` asks every
  turn for the record's JSON schema as the response format (every property
  required, `additionalProperties: false`). The answer is parsed (a Markdown
  fence around it is stripped), then `Validate` checks what a schema can't
  say. A bad answer goes back with the problems listed: first a missing
  `reply` (it doesn't parse), then an `outcome` of `denied` (it doesn't
  validate). With `MaxRepairs = 2` the third answer is the output, and
  `Turns` counts the repair turns. With the default `MaxRepairs = 1`
  (scenario 2b) the node fails: `agent 'support-agent' gave no valid answer: …`.
- **Refusals go back to the model** (scenario 3), each as `{"error": …}` in
  the agent's words, and the loop goes on: a tool that doesn't exist
  (`was not found`), a tool outside the subset (`is not permitted here`),
  arguments the tool can't read (`invalid arguments for tool 'orders_lookup':
  argument 'orderId' must be a string`), and
  arguments that aren't a JSON object. None of those tools ran.
- **Any other tool failure fails the node** (scenario 3b): the refund gateway
  throws, and the node fails with `tool 'refunds.issue' failed: …`. An
  ordinary exception is fatal, so `Retry(2)` doesn't retry it. With
  `ToolFailures.ReturnToModel` (scenario 3c) the same failure is sent to the
  model instead, recorded as `Failed`, and the model escalates.
- **`MaxTurns`** (scenario 4, a text agent, `StrataAgent<string>`). The model
  keeps polling `shipping.track`. One tool turn more than `MaxTurns = 3`
  fails the node **transiently** before that turn's call runs, so the node's
  `RecoveryPolicy.Retry(1)` runs it again. The retry **starts the
  conversation over**: its first request holds only the two prompt messages.
  The conversation lives for one attempt, and the result describes only the
  attempt that answered.
- **The agent node is an ordinary node**: `AddAgentNode(name, agent, prompt,
  apply)` builds the messages from the state and folds the result back into
  it. Here it is followed by a plain `Notify` node, and takes a recovery
  policy like any fallible node.

Some things the demo shows that are easy to miss:

- The agent and the registry read the same call differently. Arguments the
  tool can't read are `Refused` in `AgentToolCall` but `Failed` in the
  registry's `ToolCallCompleted`. Arguments that aren't a JSON object are
  refused before dispatch, so the registry never sees the call and records no
  event for it.
- "Not permitted" needs the model to use the excluded tool's Strata name
  (`accounts.close`). An excluded tool is never declared, so its provider name
  (`accounts_close`) maps to nothing and comes back as "not found".
- Only arguments the generated dispatch can't bind are refused as "invalid
  arguments": it throws `ToolArgumentsException` before the tool runs, and the
  refusal names the argument (`argument 'orderId' must be a string`). Anything
  the tool itself throws, an `ArgumentException` from a domain check included,
  is the tool's failure. Earlier 0.9 builds also refused a tool's own
  `ArgumentException`, hiding the failure (fixed before `0.9.0-preview.1`). An
  unknown order is a `null` answer, not an exception.

## What it does *not* cover

- **A real model**: [`03-llm-tool-calling`](../03-llm-tool-calling) (OpenAI,
  needs `OPENAI_API_KEY`).
- **Streaming the turns' text** under `RunStreaming`:
  [`06-token-streaming`](../06-token-streaming). This demo never streams.
- **Model faults** (a 429 or 5xx, `Retry-After`) and failing over between
  models: [`24-model-routes`](../24-model-routes). Backoff between retries:
  [`08-error-recovery`](../08-error-recovery).
- **Tool middleware**, including a middleware's denial, which the agent sends
  back as `{"error": "denied: …"}`:
  [`26-tool-registry-and-middleware`](../26-tool-registry-and-middleware).
- **`ToolConcurrency`**: a turn's calls run one after another here, the
  default.
- **Name collisions.** `ToolDeclarations` makes clashing names unique with
  `_2`, `_3` and cuts names to 64 characters; these names don't clash.
- **Run events and spans** beyond the tool events checked here:
  [`27-run-observability`](../27-run-observability).
- **Keeping a conversation** past one attempt. Strata doesn't persist it;
  [`25-long-term-memory`](../25-long-term-memory) covers memory.

## Run it

```bash
dotnet run --project demos/23-agent-tool-loop/23-agent-tool-loop.csproj
```

Takes about a second. The output is the same on every run (the token counts
are the stand-in's made-up numbers):

```
=== Scenario 1: look the order up, refund it, answer ===
  The tools as the model sees them (ToolDeclarations.For(subset)):
    orders.lookup   -> orders_lookup
    shipping.track  -> shipping_track
    refunds.issue   -> refunds_issue
  The conversation after the prompt:
    model calls orders_lookup {"orderId":"A-1001"}  (call_1)
    model calls shipping_track {"orderId":"A-1001"}  (call_2)
    tool message, 2 result(s):
      call_1: {"result":{"OrderId":"A-1001","CustomerId":"C-77","Total":39.90,"Status":"delivered, box crushed in transit","DaysSinceDelivery":3}}
      call_2: {"result":"delivered"}
    model calls refunds_issue {"orderId":"A-1001","amount":39.90,"reason":"damaged in transit"}  (call_3)
    tool message, 1 result(s):
      call_3: {"result":"RF-0001"}
    model answers: {"orderId":"A-1001","outcome":"refunded","refunded":39.90,"refundId":"RF-0001","reply":"Sorry about the crushed box. I have refunded 39.90 to your card (RF-0001)."}
  Turns: 3. Tool calls (Strata names, the model's ids):
    orders.lookup   call_1  Succeeded
    shipping.track  call_2  Succeeded
    refunds.issue   call_3  Succeeded
  ToolCallStarted events: orders.lookup call_1 caller=support-agent, shipping.track call_2 caller=support-agent, refunds.issue call_3 caller=support-agent
  Usage summed over the turns: 480 in, 116 out (the model reported 480 and 116).
  Output: RefundDecision { OrderId = A-1001, Outcome = refunded, Refunded = 39.90, RefundId = RF-0001, Reply = Sorry about the crushed box. I have refunded 39.90 to your card (RF-0001). }
  Refunds issued: RF-0001 A-1001 39.90
  Notify sent: to C-77: refunded: Sorry about the crushed box. I have refunded 39.90 to your card (RF-0001).

=== Scenario 2: an answer that doesn't parse, then one that doesn't validate -- MaxRepairs 2 ===
  Every turn asks for JSON schema 'RefundDecision': required [orderId, outcome, refunded, refundId, reply], additionalProperties false.
  The conversation after the prompt:
    model calls orders_lookup {"orderId":"A-1003"}  (call_1)
    tool message, 1 result(s):
      call_1: {"result":{"OrderId":"A-1003","CustomerId":"C-12","Total":64.50,"Status":"delivered","DaysSinceDelivery":70}}
    model answers: ```json {"orderId":"A-1003","outcome":"declined","refunded":0,"refundId":null} ```
    repair request: Your answer was not valid: it is not JSON that matches the schema (JSON deserialization for type 'RefundDecision' was missing required properties including: 'reply'.). Answer again with one JSON object that matches the schema.
    model answers: {"orderId":"A-1003","outcome":"denied","refunded":0,"refundId":null,"reply":"A-1003 was delivered 70 days ago, past our 30-day window."}
    repair request: Your answer was not valid: outcome 'denied' is not one of refunded, declined, escalated. Answer again with one JSON object that matches the schema.
    model answers: {"orderId":"A-1003","outcome":"declined","refunded":0,"refundId":null,"reply":"A-1003 was delivered 70 days ago, past our 30-day window, so I can't refund it."}
  Turns: 4, repair turns included. Output: RefundDecision { OrderId = A-1003, Outcome = declined, Refunded = 0, RefundId = , Reply = A-1003 was delivered 70 days ago, past our 30-day window, so I can't refund it. }

=== Scenario 2b: the same answers with MaxRepairs 1 -- the node fails ===
  NodeFailed at 'Resolve' after 1 attempt(s): agent 'support-agent' gave no valid answer: outcome 'denied' is not one of refunded, declined, escalated

=== Scenario 3: four bad calls in one turn -- each refused back to the model ===
  The conversation after the prompt:
    model calls orders_cancel {"orderId":"A-1001"}  (call_1)
    model calls accounts.close {"customerId":"C-77"}  (call_2)
    model calls orders_lookup {"orderId":1001}  (call_3)
    model calls refunds_issue <not a JSON object>  (call_4)
    tool message, 4 result(s):
      call_1: {"error":"tool 'orders_cancel' was not found"}
      call_2: {"error":"tool 'accounts.close' is not permitted here"}
      call_3: {"error":"invalid arguments for tool 'orders_lookup': argument 'orderId' must be a string"}
      call_4: {"error":"the arguments for tool 'refunds_issue' aren't a JSON object"}
    model calls orders_lookup {"orderId":"A-1001"}  (call_5)
    tool message, 1 result(s):
      call_5: {"result":{"OrderId":"A-1001","CustomerId":"C-77","Total":39.90,"Status":"delivered, box crushed in transit","DaysSinceDelivery":3}}
    model answers: {"orderId":"A-1001","outcome":"escalated","refunded":0,"refundId":null,"reply":"I can't close accounts. A colleague will contact you about A-1001 and your account."}
  Agent's record:  orders_cancel Refused, accounts.close Refused, orders.lookup Refused, refunds.issue Refused, orders.lookup Succeeded
  Registry events: orders_cancel Refused, accounts.close Refused, orders.lookup Failed, orders.lookup Succeeded
  Tools that actually ran: orders.lookup(A-1001)

=== Scenario 3b: the refund gateway fails -- ToolFailures.FailNode (the default), Retry(2) ===
  NodeFailed at 'Resolve' after 1 attempt(s): tool 'refunds.issue' failed: card ending 4242 is closed

=== Scenario 3c: the same failure with ToolFailures.ReturnToModel ===
  The conversation after the prompt:
    model calls orders_lookup {"orderId":"A-1001"}  (call_1)
    tool message, 1 result(s):
      call_1: {"result":{"OrderId":"A-1001","CustomerId":"C-77","Total":39.90,"Status":"delivered, box crushed in transit","DaysSinceDelivery":3}}
    model calls refunds_issue {"orderId":"A-1001","amount":39.90,"reason":"damaged in transit"}  (call_2)
    tool message, 1 result(s):
      call_2: {"error":"tool 'refunds_issue' failed: card ending 4242 is closed"}
    model answers: {"orderId":"A-1001","outcome":"escalated","refunded":0,"refundId":null,"reply":"Your card is closed, so a colleague will arrange the refund another way."}

=== Scenario 4: the model keeps polling -- MaxTurns 3, Retry(1) on the node ===
  Attempt 1 failed transiently: agent 'support-agent' made 3 tool turns without answering
  Tool calls run, by attempt: attempt 1: 3, attempt 2: 1 (the 4th turn's call never ran).
  Attempt 2's first request held 2 messages: the prompt, nothing from attempt 1.
  Turns in the result: 2 (attempt 2 only). Answer: Your parcel A-1002 has left the Rotterdam hub and is on its way. There is no delivery date yet.

=== Done: tool calls dispatched and answered, a typed answer repaired, refusals told to the model, and a runaway loop retried from scratch. ===
```

The demo checks each claim as it runs (the declarations, the tool message
order, the Strata names in events, the summed usage, the schema, the repair
requests, every refusal's wording, which tools actually ran, the attempts and
the fresh conversation on retry) and exits non-zero if one fails.

## Files

- `Program.cs` — the scenarios, top-level statements
- `ScriptedModel.cs` — the stand-in model: a scripted `IChatClient` that
  records every request
- `SupportDesk.cs` — the four `[StrataTool]` tools over an in-memory order
  book
- `23-agent-tool-loop.csproj` — references `Datalium.Strata`,
  `Datalium.Strata.AI` (which brings `Microsoft.Extensions.AI.Abstractions`)
  and `Datalium.Strata.Storage` from NuGet; `Datalium.Strata` brings the source
  generator for the `[StrataTool]` methods
