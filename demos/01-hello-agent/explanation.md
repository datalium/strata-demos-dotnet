# 01-hello-agent, explained

This is a guided walkthrough of this demo's one source file,
[`Program.cs`](Program.cs), its project file, and the Strata features they
use. For a shorter summary, see [`README.md`](README.md).

## The big picture in one paragraph

This is the smallest possible Strata program. It builds a **graph** with one
step (a **node**) named `"Greet"`. It starts the graph with the **state**
`{ Name = "World" }`. The node reads `Name` and writes
`Greeting = "Hello, World!"`, and the program prints the greeting. There are
no tools, no AI, and no waiting for a human. Its purpose is to show the basic
pattern every later demo builds on: **build → compile → run → check the
result**.

```
  initial state                    node "Greet"                      final state
 ┌──────────────────┐       ┌───────────────────────────┐       ┌──────────────────────────┐
 │ Name     = World │ ───►  │ state with {              │ ───►  │ Name     = World         │
 │ Greeting = null  │       │   Greeting = "Hello, …!"  │       │ Greeting = Hello, World! │
 └──────────────────┘       │ }                         │       └──────────────────────────┘
                            └───────────────────────────┘
```

### The four Strata building blocks

| Word | Meaning in Strata | In this demo |
|---|---|---|
| **State** | The data that flows through the graph. Each step receives it and returns an updated copy. | `HelloState` (`Name`, `Greeting`) |
| **Node** | One named step: a function that takes the state and returns a new state. | `"Greet"` |
| **Edge** | A link that says which node runs next. | None, because there's only one node. |
| **Graph** | Nodes and edges together, plus a starting node called the **entry point**. | `StateGraph<HelloState>` |

---

## `Program.cs`, step by step

The file uses C# **top-level statements**: there's no `class Program` or
`static void Main`. The code at the top of the file *is* the program, and type
declarations such as `HelloState` must come after it.

### Step 1: imports

```csharp
using Datalium.Strata.Graph;         // StateGraph, CompiledGraph
using Datalium.Strata.Interruption;  // ICheckpointStore, GraphExecutionResult
using Datalium.Strata.Storage;       // InMemoryCheckpointStore
```

`Console`, `InvalidOperationException`, and other common types need no
`using` line because the `.csproj` turns on `ImplicitUsings`.

### Step 2: build and compile the graph

```csharp
var graph = new StateGraph<HelloState>()
    .AddNode("Greet", state => state with { Greeting = $"Hello, {state.Name}!" })
    .Compile();
```

These three calls are chained together (a "fluent" builder):

- **`new StateGraph<HelloState>()`**: creates an empty graph *builder*. The
  type parameter `<HelloState>` fixes the state type. Every node in this
  graph receives a `HelloState` and must return a `HelloState`, and the
  compiler enforces that.
- **`.AddNode("Greet", state => ...)`**: adds a node.
  - `"Greet"` is the node's **name**. Strata uses names for routing,
    checkpoints, and error messages.
  - The lambda is the node's **handler**, of type `Func<HelloState,
    HelloState>`: a **plain synchronous function**, the simplest kind. The
    engine runs it on the thread pool. Every handler kind also has an
    **async** form, `Func<TState, NodeContext, CancellationToken,
    ValueTask<TState>>`, which can `await` and is told about its run (thread
    id, node path, attempt, …). Demo [`02-tool-calling`](../02-tool-calling)
    is the first to use it.
  - `state with { Greeting = ... }` makes a **copy** of the state with only
    `Greeting` changed. The original isn't modified. Every Strata node follows
    this rule: return a new state, never mutate the old one.
  - The **first node added becomes the entry point** automatically.
    `SetEntryPoint(...)` exists if you need a different one.
- **`.Compile()`**: turns the builder into a runnable
  `CompiledGraph<HelloState>`. It first **checks the graph's structure**
  (STRATA-SPEC-004; see [Compile-time validation](#compile-time-validation-strata-spec-004)
  below). If the graph is broken, `Compile()` **throws** a
  `GraphCompilationException`. It does not return an error value, so mistakes
  in a graph's shape stop the program at startup, before any node runs.

### Step 3: create a checkpoint store

```csharp
// RunAsync always takes an ICheckpointStore. By default a run writes a
// checkpoint only where it stops early (an interrupt, a wait, a failure, a
// cancellation), and this one never does, so the store stays empty.
ICheckpointStore store = new InMemoryCheckpointStore();
```

- **Checkpoints** are saved snapshots of a run. By default Strata writes one
  when a run **pauses** (for example, to wait for human approval), **fails**
  or is **cancelled**, so the run can be inspected or resumed later.
- **`ICheckpointStore`** is the interface. At its core are two methods,
  `Save(checkpoint)` and `Load(threadId)`; the engine calls their async
  forms, `SaveAsync` and `LoadAsync`, which by default just call the sync
  ones. Different storage back ends implement it.
- **`InMemoryCheckpointStore`** (from `Datalium.Strata.Storage`) is the
  simplest one: a dictionary in memory, lost when the program exits.
- **Why pass one here?** `RunAsync` requires a store for every run. This
  graph can't pause and won't fail, so nothing is ever saved: by default a
  successful run writes no checkpoint. Checkpoints first matter in
  [`05-hitl-interruption`](../05-hitl-interruption), and
  [`12-pluggable-storage`](../12-pluggable-storage) swaps in other stores.
  A graph compiled with `CheckpointPolicy.EveryNode` writes one after every
  node instead, so a crashed run can be recovered; see
  [`20-durable-execution`](../20-durable-execution).

### Step 4: run the graph

```csharp
var result = await graph.RunAsync("hello-thread", new HelloState { Name = "World" }, store);
```

`RunAsync(threadId, initialState, store)` takes three arguments:

| Argument | Value | Meaning |
|---|---|---|
| `threadId` | `"hello-thread"` | A name for this run (a "thread" of conversation). Checkpoints are saved under it, so a paused run can be found and resumed later. |
| `initialState` | `new HelloState { Name = "World" }` | The state the entry node receives. |
| `store` | the in-memory store | Where checkpoints would go. |

What happens inside:

1. Start at the entry point, `"Greet"`.
2. Call its handler with the initial state and keep the returned state.
3. Look for an outgoing edge. There is none, so `"Greet"` is a **terminal
   node** and the run is finished.
4. Return `Completed(finalState)`.

**Why `await`?** The executor is asynchronous: it awaits the checkpoint
store, async node handlers, and the thread-pool work item a sync handler like
`"Greet"` runs on. `RunAsync` also takes an optional `CancellationToken` as a
last argument, and an overload with `RunOptions` (metadata, a deadline, an
observer, …). This demo needs neither; see
[`15-async-and-cancellation`](../15-async-and-cancellation).

### Step 5: handle the result

```csharp
switch (result)
{
    case GraphExecutionResult<HelloState>.Completed completed:
        Console.WriteLine(completed.State.Greeting);
        break;
    case GraphExecutionResult<HelloState>.Failed failed:
        throw new InvalidOperationException($"graph execution failed: {failed.Message}");
    default:
        throw new InvalidOperationException($"unexpected result: {result}");
}
```

`RunAsync` returns a **`GraphExecutionResult<HelloState>`**. Expected outcomes
come back as values, not exceptions. These are the cases:

| Case | Meaning | In this demo |
|---|---|---|
| `Completed(State)` | The graph reached its end. `State` is the final state. | ✅ This is what happens. It prints `Hello, World!`. |
| `Failed(Message)` | The run couldn't continue, for example because no edge condition matched. More specific subtypes: `NodeFailed` (a fallible node ran out of recovery options, [demo 08](../08-error-recovery)) and `HopLimitExceeded` (the loop circuit breaker tripped, [demo 16](../16-loop-circuit-breaker)). | Shouldn't happen. |
| `Interrupted(...)` | The run **paused** at an interrupt boundary or a wait and saved a checkpoint, to be resumed later. | Impossible: the graph has no boundary and no wait. |
| `Cancelled(...)` | The run's `CancellationToken` fired or its deadline passed. A checkpoint was saved so the run can be resumed. | Impossible: no token or deadline is given. |

The `default` branch catches the two impossible cases, so a change that makes
one of them possible fails loudly instead of printing nothing.

The C# `switch` uses **type patterns**: `case ... Completed completed:` checks
the type and assigns the variable in one step.

> **Note:** an ordinary exception thrown *inside* a node handler (say, a
> `NullReferenceException`) is **not** turned into `Failed`. It propagates
> out of `RunAsync` like any other exception. Turning node errors into
> results and retries takes "fallible" nodes, covered in
> [`08-error-recovery`](../08-error-recovery).

### Step 6: the state type

```csharp
sealed record HelloState
{
    public required string Name { get; init; }
    public string? Greeting { get; init; }
}
```

- **`record`**: a C# data type made for immutable values. It supports the
  `with { ... }` copy syntax the node uses, and two records with the same
  values compare equal.
- **`sealed`**: nothing can inherit from it. That's a good default for plain
  data.
- **`required string Name`**: the caller *must* set `Name` when creating the
  state (`new HelloState { Name = "World" }`). The compiler refuses to build
  the code otherwise.
- **`string? Greeting`**: nullable, because it's empty (`null`) until the
  `"Greet"` node fills it in.
- **`init`**: properties can be set only while the object is being created,
  which keeps the state immutable. Any change has to go through `with`.
- **Keep state as plain data.** When Strata does save a checkpoint, it
  serializes the state to JSON with `System.Text.Json`, so the state should be
  plain values: strings, numbers, lists, and nested records.

---

## Compile-time validation (STRATA-SPEC-004)

`Compile()` runs `GraphValidator.Validate` over the graph's shape (nodes and
edges only, not the code inside handlers). It checks four rules:

| Error code | Rule | Why this demo passes |
|---|---|---|
| `STRATA_ERR_MISSING_ENTRY` | The entry point must be a real node. | `"Greet"` was added first, so it's the entry point. |
| `STRATA_ERR_DANGLING_EDGE` | Every edge must connect two existing nodes. | There are no edges. |
| `STRATA_ERR_ORPHAN` | Every node must be reachable from the entry point. | The only node *is* the entry point. |
| `STRATA_ERR_UNGUARDED_CYCLE` | A loop must include at least one **conditional** edge, otherwise it would run forever. | There are no edges, so there are no loops. |

Parallel groups and maps add checks of their own (an empty group, a duplicate
branch name, an unreachable quorum, a wait inside a map); this graph has
neither. All errors found are collected into one `GraphCompilationException`,
with a message such as:

```
Graph compilation failed:
[STRATA_ERR_ORPHAN] Node 'Unused' is isolated and unreachable from the entry point 'Greet'.
```

The same rules and codes are used by Strata's Rust engine and by the
`strata graph validate` CLI. Demos [`07-graph-validation`](../07-graph-validation)
and [`14-graph-ci-gate`](../14-graph-ci-gate) cover them in depth.

---

## The project file: `01-hello-agent.csproj`

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <RootNamespace>Datalium.Strata.Demos.HelloAgent</RootNamespace>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Datalium.Strata" />
  <PackageReference Include="Datalium.Strata.Storage" />
</ItemGroup>
```

| Setting | Meaning |
|---|---|
| `OutputType=Exe` | Builds a runnable console app. |
| `TargetFramework=net10.0` | Targets .NET 10. |
| `ImplicitUsings` | Adds common `using`s (`System`, `System.Linq`, `System.Threading.Tasks`, …) automatically. |
| `Nullable` | Turns on nullable reference types, which is why `Greeting` is declared `string?`. |
| `Datalium.Strata` | The core engine: `StateGraph`, `CompiledGraph`, `GraphExecutionResult`, `ICheckpointStore`, validation. |
| `Datalium.Strata.Storage` | Storage back ends, here `InMemoryCheckpointStore`. |

Strata is referenced as **NuGet packages** from nuget.org. The references
carry no version: the repo sets every package version once, in
[`Directory.Packages.props`](../../Directory.Packages.props) (central package
management), where Strata's packages share `StrataVersion`, `0.9.0-preview.1`.

The `Datalium.Strata` package also carries the `[StrataTool]` source
generator. This demo has no `[StrataTool]` methods, so it does nothing here;
demo [`02-tool-calling`](../02-tool-calling) is the first one that uses it.

---

## Strata features used, at a glance

| Feature | Spec | Where it's used |
|---|---|---|
| `StateGraph<TState>` builder | Core graph | Creates the graph |
| `AddNode(name, handler)` | Core graph | Adds `"Greet"` |
| First node = entry point | Core graph | `"Greet"` is where the run starts |
| Immutable state + `with` | Core graph | The node returns a new `HelloState` |
| `Compile()` with validation, throws `GraphCompilationException` | STRATA-SPEC-004 | Checks the graph's shape before running |
| `CompiledGraph.RunAsync(threadId, state, store)` | Core graph / STRATA-SPEC-002 | Runs the graph |
| Terminal node (no outgoing edges) = completion | Core graph | The run ends after `"Greet"` |
| `GraphExecutionResult` (`Completed` / `Failed` / `Interrupted` / `Cancelled`) | STRATA-SPEC-002 | The `switch` over the outcome |
| `ICheckpointStore` / `InMemoryCheckpointStore` | STRATA-SPEC-002 | Required by `RunAsync`; never written to here |

## What this demo deliberately does *not* do

- **No edges or multiple nodes**: see [`04-multi-node-graph`](../04-multi-node-graph).
- **No tools**: see [`02-tool-calling`](../02-tool-calling).
- **No LLM**: see [`03-llm-tool-calling`](../03-llm-tool-calling).
- **No pausing for a human** (`AddInterruptBoundary`): see
  [`05-hitl-interruption`](../05-hitl-interruption).
- **No streaming**: see [`06-token-streaming`](../06-token-streaming).
- **No async handler, cancellation or deadline**: see
  [`02-tool-calling`](../02-tool-calling) for an async node and
  [`15-async-and-cancellation`](../15-async-and-cancellation) for the rest.
- **No `CompilationOptions`**: `Compile()` with no arguments sets no hop
  limit ([demo 16](../16-loop-circuit-breaker)), no graph identity
  ([demo 22](../22-graph-identity)) and the default checkpoint
  policy ([`20-durable-execution`](../20-durable-execution) changes it).

## Try it yourself

```bash
dotnet run --project demos/01-hello-agent/01-hello-agent.csproj
# Hello, World!
```

Some experiments that show the features working:

1. **Change the input**: `new HelloState { Name = "Strata" }` prints
   `Hello, Strata!`.
2. **Remove `Name = "World"`**: the C# compiler rejects it, because `Name` is
   `required`.
3. **Add a second, unconnected node** with
   `.AddNode("Unused", s => s)` before `.Compile()`. `Compile()` throws
   `[STRATA_ERR_ORPHAN] Node 'Unused' is isolated and unreachable from the entry point 'Greet'.`
4. **Connect it** with `.AddEdge("Greet", "Unused")`. The graph compiles
   again, and `"Unused"` runs after `"Greet"`.
5. **Make a loop** with `.AddEdge("Greet", "Greet")`. `Compile()` throws
   `STRATA_ERR_UNGUARDED_CYCLE`, because the loop has no condition that could
   ever stop it.
6. **Compile an empty graph**: `new StateGraph<HelloState>().Compile()` throws
   `STRATA_ERR_MISSING_ENTRY`, because there's no node to start from.
