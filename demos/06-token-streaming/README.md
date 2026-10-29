# 06 — token-streaming

Token streaming (STRATA-SPEC-001) of a live model answer through
`Datalium.Strata.AI` (`design/44-LLM-Integration.md`). The `AnswerQuestion`
node is a model node (`.AddModelNode(...)`): it makes one call to OpenAI
through `StrataChat`, which streams because the run streams
(`RunStreaming`) and sends each piece of text to the run's stream as a
`Token` chunk. The consumer prints the answer as it arrives, and the node
still gets the whole answer back as one `ChatResponse`. A second run asks
for a model that doesn't exist, to show a provider fault failing the node.
Pairs with [`03-llm-tool-calling`](../03-llm-tool-calling): the same OpenAI
client, a model node instead of an agent.

This demo makes real, billed calls to the OpenAI API. It and demo 03 are
the only demos that do.

## What it demonstrates

- `.AddModelNode(name, client, prompt, apply, options)`: a model call as a
  node, with no streaming code in it. The node's `prompt` builds the
  messages from the state; its `apply` folds the `ChatResponse` into the
  next state (`response.Text`)
- `CompiledGraph.RunStreaming(...)` returning
  `(ChannelReader<StreamChunk> Reader, Task<GraphExecutionResult<TState>> Completion)`.
  Because the run streams, the model node's `NodeContext` has a stream, and
  `StrataChat` calls `GetStreamingResponseAsync` instead of
  `GetResponseAsync`. Under `RunAsync` the same node makes one plain
  request
- The two views of one answer: the consumer sees many `Token` chunks while
  the node runs; the node sees the whole text once the stream ends. The demo
  checks that the tokens, joined in order, are exactly the node's answer
- `NodeStart`/`NodeEnd` chunks for every node, streaming or not:
  `SummarizeAnswer` is a plain node after the model node, and it reads the
  whole answer from the state
- A `RecoveryPolicy` on the model node
  (`Retry(2).WithBackoff(1s, 10s)`). A transient fault (429, 5xx, a stream
  that breaks part-way) would retry the node after the provider's retry
  hint, and the stream would carry a `Retry` chunk before the new attempt's
  tokens. The consumer clears what it collected when it sees one
- A provider fault as a node failure: asking OpenAI for
  `strata-demo-no-such-model` gets a 4xx back. `ModelFaults` reads the
  SDK's `ClientResultException` as **fatal** (any 4xx except 408, 409 and
  429 is the request's fault), so the run ends `NodeFailed` after **one**
  attempt, the policy's two retries unused, and the stream has no `Retry`
  and no `Token` chunks. The fault is real, from OpenAI, not a fake
- The OpenAI SDK's own retries off (`ClientRetryPolicy(maxRetries: 0)`),
  so the node's policy is the only one retrying, as design/44 advises

## What it does *not* cover

- A transient fault actually being retried: OpenAI can't be made to return
  a 429 or break a stream on demand. Demos
  [`08-error-recovery`](../08-error-recovery) (retries and backoff) and
  [`24-model-routes`](../24-model-routes) (failing over between models, with
  a scripted model) cover retrying
- Backpressure under a full channel: [`11-backpressure`](../11-backpressure)
- Tool calls: [`03-llm-tool-calling`](../03-llm-tool-calling) (live) and
  [`23-agent-tool-loop`](../23-agent-tool-loop) (scripted). An agent's turns
  stream the same way when its run streams
- The run events and telemetry a model call records (`ModelCallStarted`,
  `ModelCallCompleted` with token usage, a `chat <model>` span):
  [`24-model-routes`](../24-model-routes) reads the model-call events, and
  [`27-run-observability`](../27-run-observability) covers run events in
  general
- Resuming a streaming run after an interrupt: `ResumeStreaming(...)`
  exists, but this graph has no interrupt boundary

## Setup

Same as demo 03. Requires an OpenAI API key with access to the Chat
Completions API:

```bash
export OPENAI_API_KEY=sk-...
# optional, defaults to gpt-4o-mini
export OPENAI_MODEL=gpt-4o-mini
```

On Windows PowerShell: `$env:OPENAI_API_KEY = "sk-..."`. See
[demo 03's README](../03-llm-tool-calling/README.md#setup) for making it
persist.

Without `OPENAI_API_KEY` set, the demo prints a short setup message to
stderr and exits with code 1, before making any network call.

## Run it

```bash
dotnet run --project demos/06-token-streaming/06-token-streaming.csproj
```

Expected output, from a live run against OpenAI (`gpt-4o-mini`, the default
model, 2026-10-24). The answer's text, its length and the number of token
chunks vary from run to run, since it's a live model; the answer prints as
one line, as it streams:

```
=== Streaming a live answer, one token at a time ===

[node start] AnswerQuestion
Rust's borrow checker enforces memory safety at compile time through ownership and a set of strict rules about borrowing variables, which eliminates data races and memory leaks without needing a garbage collector. In contrast, garbage collection dynamically manages memory at runtime, automatically reclaiming memory that is no longer in use, but it can introduce latency due to stop-the-world pauses and may not provide the same guarantees against data races. This compile-time checking allows Rust to achieve performance close to that of C or C++ while ensuring safety.
[node end]   AnswerQuestion
[node start] SummarizeAnswer
[node end]   SummarizeAnswer

=== Done ===
token_chunks: 101
answer_char_count: 571

=== A provider fault: asking for model 'strata-demo-no-such-model' ===

node failed: AnswerQuestion, after 1 attempt(s)
message: model call failed (404): HTTP 404 (invalid_request_error: model_not_found)
chunks the consumer saw: NodeStart, Error
```

OpenAI answers the unknown model with a 404 (`model_not_found`); the demo
only checks for a 4xx and a single attempt. The transient paths can't be
triggered on demand against the real API, so they were checked with the
OpenAI client pointed at a local stand-in: a 429 with `retry-after-ms` on the
first request, and a stream cut off half-way, each printed
`[retry]      AnswerQuestion` and then the whole answer, and the checks passed.

## Files

- `Program.cs` — the whole demo: the OpenAI client, the graph (model node,
  recovery policy, plain node), the two runs and their checks
