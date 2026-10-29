# 01 — hello-agent

The smallest possible Strata program: a one-node graph that reads `Name`
from its state and writes a `Greeting`. No tools, no streaming, no
human-in-the-loop — just enough to prove the engine and the wiring to
Strata's NuGet packages both work.

## What it demonstrates

- `new StateGraph<TState>()` + `.AddNode(...)` — the fluent graph builder
- `.Compile()` running STRATA-SPEC-004's structural validation (entry-point,
  dangling-edge, orphan and unguarded-cycle checks) before anything executes —
  note it *throws* `GraphCompilationException` on failure, unlike Rust's
  `Result`-returning `.compile()`
- `.RunAsync(threadId, initialState, store)` executing the graph and
  returning a `GraphExecutionResult<TState>`
- Why `RunAsync` always needs an `ICheckpointStore` argument even though this
  graph never stops early — see `Program.cs`'s comment
- The simplest handler kind, a synchronous `Func<TState, TState>`. The engine
  runs it on the thread pool. Every handler kind also has an async form that
  gets a `NodeContext` and a `CancellationToken`; that comes in
  [`02-tool-calling`](../02-tool-calling)

## What it does *not* cover

- Tools and async nodes: [`02-tool-calling`](../02-tool-calling)
- A real LLM call: [`03-llm-tool-calling`](../03-llm-tool-calling)
- Edges, branching and `.SetEntryPoint(...)`:
  [`04-multi-node-graph`](../04-multi-node-graph)
- Pausing for a human (`.AddInterruptBoundary(...)`):
  [`05-hitl-interruption`](../05-hitl-interruption)
- Streaming: [`06-token-streaming`](../06-token-streaming)
- `.Compile()` rejecting a graph: [`07-graph-validation`](../07-graph-validation)
- Cancellation, deadlines and timeouts:
  [`15-async-and-cancellation`](../15-async-and-cancellation)
- `Compile(new CompilationOptions { ... })`: a hop limit
  ([`16-loop-circuit-breaker`](../16-loop-circuit-breaker)), a graph identity
  ([`22-graph-identity`](../22-graph-identity)), and checkpointing after every node
  ([`20-durable-execution`](../20-durable-execution))

## Run it

```bash
dotnet run --project demos/01-hello-agent/01-hello-agent.csproj
```

Expected output:

```
Hello, World!
```

## Files

- `Program.cs` — the whole demo, top-level statements + one `record` for state
- `explanation.md` — a line-by-line walkthrough
