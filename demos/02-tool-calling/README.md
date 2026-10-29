# 02 — tool-calling

A node dispatches an `add(a, b)` tool by name through the source-generated
`IStrataToolProvider`, instead of computing the sum inline. This is
STRATA-SPEC-003's tool-calling metaprogramming: no runtime reflection, no
schema drift between what the tool declares and what actually gets called.
The node is async, so it awaits the tool and passes it a `ToolCallContext`
built from its own `NodeContext`
(`design/20-Tool-Dispatch-And-Registry.md`), which tells the tool which
thread and node called it.

## What it demonstrates

- `[StrataTool(Name = "...", Description = "...")]` on a `static` method in a
  `partial class` (`Tools.cs`) — `Datalium.Strata.SourceGenerators` emits a
  second partial declaration of the same class implementing
  `IStrataToolProvider`, with a compile-time JSON-Schema manifest and a
  dispatch `switch` that calls the method directly
- Method **parameters** (`double a, double b`) become the tool's schema
  fields automatically — unlike the Rust demo, there's no separate arguments
  struct to declare by hand, the generator synthesizes one
- An **async node** (`AddNode(name, async (state, ctx, ct) => ...)`) that
  awaits the tool. The sync `Func<TState, TState>` form from
  [`01-hello-agent`](../01-hello-agent) would have to block on the tool's
  `Task`
- The four-argument `DispatchToolAsync(name, jsonArgs, context, ct)` with
  `ToolCallContext.From(ctx)`: the context carries the thread id, run id,
  node path, attempt, run metadata, span and services. The run's
  cancellation token goes with it
- A `ToolCallContext` parameter on the tool itself (`AddAsync(a, b, context)`).
  The generator fills it from the dispatch call and leaves it out of the
  manifest; the demo checks that the manifest still lists only `a` and `b`.
  The tool uses the context for an audit line and refuses a call with no
  caller, which is what the two-argument `DispatchToolAsync` (it passes
  `ToolCallContext.None`) would give it
- The result comes back wrapped as `{"result": ...}`
- Where the generator comes from: it ships inside the `Datalium.Strata`
  package, so the plain `<PackageReference Include="Datalium.Strata" />` in
  `02-tool-calling.csproj` is all it takes. The project that declares the
  tools must reference `Datalium.Strata` itself: a package's analyzers don't
  pass on through a `ProjectReference` (see the comment in the `.csproj`)

## What it does *not* cover

- **No LLM.** The tool's arguments come from the graph's own state.
  `GetToolsManifest()` is what you'd hand a model's tool-calling API; see
  [`03-llm-tool-calling`](../03-llm-tool-calling) for a live call, and
  [`23-agent-tool-loop`](../23-agent-tool-loop) for the agent loop that
  `Datalium.Strata.AI` builds around a model and its tools.
- **No `ToolRegistry` or middleware.** The node calls the generated provider
  directly. Registering several providers in one `ToolRegistry`, a per-agent
  `Subset(...)`, tool middleware, and the `strata.tool` span and
  `ToolCallStarted` / `ToolCallCompleted` run events that a registry
  dispatch adds are in
  [`26-tool-registry-and-middleware`](../26-tool-registry-and-middleware).
- **No cancellation scenario.** The token is passed through, but nothing
  cancels it; see
  [`15-async-and-cancellation`](../15-async-and-cancellation).

## Run it

```bash
dotnet run --project demos/02-tool-calling/02-tool-calling.csproj
```

Expected output:

```
[add] called by node 'Add' on thread 'calc-thread', attempt 1
3 + 5 = 8
manifest parameters for 'add': a, b
```

The first line is printed by the tool, from the context it was given. The
program exits with code 1 if the sum or the manifest check is wrong.

## Files

- `Tools.cs` — the `[StrataTool]`-annotated method
- `Program.cs` — graph definition, execution and the manifest check
- `explanation.md` — a line-by-line walkthrough, including the generated code
