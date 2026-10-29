# 27 — run-observability

Run metadata, run events and telemetry (`design/18-Run-Observability.md`):
three ways to see what a run did, all fed by the same engine hooks. Caller
metadata (`tenant`, `ticket`) rides on every checkpoint. An observer receives
typed `RunEvent`s for a run with a retry and an interrupt, then for its resume.
Two badly behaved observers show that an observer can't fail or stall a run.
And the engine's `ActivitySource` and `Meter`, read with plain .NET listeners,
give the span tree and the metrics.

Everything is deterministic: the "transient failure" is a counter, and the
demo prints counts, order and tags, never durations. The Rust twin is
[`strata-demos-rust/demos/27-run-observability`](https://github.com/datalium/strata-demos-rust/tree/main/demos/27-run-observability)
(`tracing` spans and the `metrics` facade where .NET has an `ActivitySource`
and a `Meter`); its README says where the two engines differ.

## What it demonstrates

- **Run metadata on every checkpoint, at every level** (scenario 1).
  `RunOptions.Metadata` is stamped on each checkpoint the run writes, and on
  the nested checkpoint of a subgraph (`Work`) inside it. The graph is compiled
  with `CheckpointPolicy.EveryNode` (from `design/33-Durable-Execution.md`) so
  the thread's log (`GetLogAsync`) has a checkpoint per node to look at,
  including two-level ones
- **A resume carries the metadata forward and overrides it key by key.** The
  resume passes only `ticket=T-1002`; the checkpoints it writes, and
  `NodeContext.Metadata` in `Work/Send`, have `tenant=acme` (carried) and
  `ticket=T-1002` (overridden). The earlier checkpoints keep `T-1001`
- **Reserved keys are refused** before anything is written: the engine's own
  keys (`error`, `error_kind`, `attempts`, `cancel_reason`) and anything under
  `strata.`. The log shows why the prefix is kept: the engine already writes
  `strata.hops` (the hop count a resume continues from,
  `design/24-Loop-Circuit-Breaker.md`) into the same map
- **Typed run events** (scenario 2), through `RunEventChannel` (one per run,
  read with `await foreach`): `RunStarted` (with the metadata), `NodeStarted`,
  `RetryScheduled` (the failed attempt, its error and the backoff delay, zero
  here), `NodeCompleted`, `CheckpointSaved`, `Interrupted`, `RunCompleted`, and
  for the resume the stamp, `Resumed` (who approved) and the rest. Each event
  has a thread id, a run id, a per-run sequence (1, 2, 3, ... with no gaps), a
  node path and an attempt. The run id is a generated UUID v7 unless the caller
  supplies one (`RunOptions.RunId`, here `resume-order-2001`); a run and its
  resume are two runs, with two ids
- **An observer can't fail or stall the run** (scenario 3). One that throws on
  its first event leaves the run's result alone, still gets every later event,
  and is counted by the `strata.observer.errors` metric. One that is stuck
  inside `OnEvent` (a `ManualResetEventSlim`) has received nothing when
  `RunAsync` returns; released, it receives all nine events in order. Delivery
  runs off the executor through an unbounded per-run queue
- **The span tree** (scenario 4), from the `Datalium.Strata` `ActivitySource`
  (`StrataTelemetry.Name`) with a plain `ActivityListener`: `strata.run` ->
  `strata.node` (`Classify`) -> `strata.attempt` (`Classify #1`, `Classify #2`).
  The failed attempt has `error.type=transient` and an error status, the node
  span a `retry` event and `strata.retry_count`, the run span the metadata
  (`strata.metadata.tenant`), the run id and the outcome. The resume is its own
  trace, rooted at `strata.resume`
- **A handler's own spans and tags.** The handler runs inside its attempt's
  span, so a span it opens (`classify-model-call`, from its own
  `ActivitySource`) nests under that attempt, and `NodeContext.Activity`
  lets it tag the attempt (`order.risk=low`)
- **Metrics** from the same `Meter` with a plain `MeterListener`:
  `strata.graphs.started` by mode, `strata.node.retries` by node and error
  type, `strata.graph.transition`, `strata.node.duration` (one per node, its
  retries included) and `strata.checkpoint.latency` by operation. The demo
  prints which measurements were recorded and their tags, not their values in
  seconds

No OpenTelemetry package is needed for any of this: the listeners are in
`System.Diagnostics`. An OpenTelemetry SDK would subscribe with
`AddSource(StrataTelemetry.Name)` / `AddMeter(StrataTelemetry.Name)`.

## What it does *not* cover

- **An exporter.** Nothing is sent to a collector; the demo prints what a
  listener received. The hosting package's `AddInstrumentation()` wires the
  source and meter into OpenTelemetry; no demo shows it yet
  ([`28-hosting`](../28-hosting) leaves it out too)
- **The other events**: parallel branches (`BranchStarted`,
  `BranchCompleted`, `JoinDecided`), maps (`MapExpanded`), leases, the hop
  limit, recovery, and tool and model calls. Demos that use those features
  show them: [`26-tool-registry-and-middleware`](../26-tool-registry-and-middleware)
  prints `ToolCallStarted` / `ToolCallCompleted`
- **Streaming runs.** `RunStreaming` / `ResumeStreaming` emit the same
  sequence; this demo uses `RunAsync` / `ResumeAsync`
- **Querying threads by metadata** (`ThreadQuery.Metadata`,
  `strata thread list --meta`)
- **Trace links across a freeze.** The resume's trace isn't linked to the
  run's; the engine doesn't build that yet

## Run it

```bash
dotnet run --project demos/27-run-observability/27-run-observability.csproj
```

Expected output. Only the generated run id of scenario 2 varies, so the demo
checks it is a UUID v7 and prints a placeholder:

```
=== Scenario 1: run metadata on every checkpoint ===
  Run with tenant=acme, ticket=T-1001: interrupted at Work/Approve.
  Resumed with ticket=T-1002 only: completed.
  Work/Send saw NodeContext.Metadata = tenant=acme, ticket=T-1002
  The thread's checkpoint log (7 checkpoints; '-> child' is the subgraph's nested checkpoint):
     0. Running     next=Prepare  strata.hops=0, tenant=acme, ticket=T-1001
     1. Running     next=Work     strata.hops=1, tenant=acme, ticket=T-1001
     2. Running     next=Work     strata.hops=1, tenant=acme, ticket=T-1001
          -> child Running     next=Approve  strata.hops=1, tenant=acme, ticket=T-1001
     3. Interrupted next=Work     strata.hops=1, tenant=acme, ticket=T-1001
          -> child Interrupted next=Send     strata.hops=2, tenant=acme, ticket=T-1001
     4. Running     next=Work     strata.hops=1, tenant=acme, ticket=T-1002
          -> child Running     next=Send     strata.hops=2, tenant=acme, ticket=T-1002
     5. Running     next=Finish   strata.hops=2, tenant=acme, ticket=T-1002
     6. Completed   next=-        strata.hops=3, tenant=acme, ticket=T-1002
  (strata.hops is the engine's own key -- the hop count it resumes with -- which is why the strata. prefix is reserved.)
  Metadata key 'error': refused -- run metadata key 'error' is reserved
  Metadata key 'attempts': refused -- run metadata key 'attempts' is reserved
  Metadata key 'strata.trace_id': refused -- run metadata key 'strata.trace_id' is reserved
  Nothing was written for the refused runs.

=== Scenario 2: run events across a retry, an interrupt and a resume ===
  RunAsync -- thread order-2001, run id a generated UUID v7:
    #1 RunStarted Run at Classify  metadata: tenant=acme
    #2 NodeStarted Classify#1
    #3 RetryScheduled Classify#1  error: gateway timeout, delay: 0 ms
    #4 NodeCompleted Classify#2 Committed
    #5 NodeStarted Approve#1
    #6 NodeCompleted Approve#1 Committed
    #7 CheckpointSaved Interrupted
    #8 Interrupted at Approve, next Execute
    #9 RunCompleted Interrupted
  ResumeAsync -- thread order-2001, run id resume-order-2001:
    #1 RunStarted Resume at Execute  metadata: tenant=acme
    #2 CheckpointSaved Interrupted (resume stamp)
    #3 Resumed by reviewer-7  from Interrupted at Approve, approved: True
    #4 NodeStarted Execute#1
    #5 NodeCompleted Execute#1 Committed
    #6 RunCompleted Completed

=== Scenario 3: an observer can't fail or stall the run ===
  Throwing observer: the run ended interrupted; the observer threw on RunStarted,
    still got the other 8 events, and strata.observer.errors counted 1.
  Stuck observer: RunAsync returned (interrupted) while the observer had taken in 0 events.
    Once unblocked, it received all 9, in sequence order.

=== Scenario 4: telemetry -- the span tree and the metrics ===
  RunAsync's trace (display name [operation name] tags):
    strata.run [strata.run]  strata.outcome=interrupted strata.metadata.tenant=acme strata.run_id=run-4001 event:checkpoint.saved
      Classify [strata.node]  strata.retry_count=1 event:retry
        Classify #1 [strata.attempt]  error.type=transient status=Error
          classify-model-call [classify-model-call]
        Classify #2 [strata.attempt]  order.risk=low
          classify-model-call [classify-model-call]
      Approve [strata.node]  strata.retry_count=0
        Approve #1 [strata.attempt]
  ResumeAsync's trace (a separate trace: there is no link across the freeze):
    strata.resume [strata.run]  strata.outcome=completed strata.metadata.tenant=acme strata.run_id=resume-4001 event:checkpoint.saved
      Execute [strata.node]  strata.retry_count=0
        Execute #1 [strata.attempt]
  Metrics recorded during that run and resume:
    strata.graphs.started      run=1 resume=1
    strata.node.retries        1 at Classify (error.type=transient)
    strata.graph.transition    Classify -> Approve
    strata.node.duration       3 measurements: Classify (committed), Approve (committed), Execute (committed)
    strata.checkpoint.latency  save interrupted, load interrupted, save interrupted

=== Done: metadata on every checkpoint, typed events per run, and spans and metrics from the same hooks. ===
```

The demo checks each claim above (the metadata on every level of every
checkpoint, the refused keys, the exact event sequences, gapless sequence
numbers, the run ids, both observers, the span parents and tags, and the
metrics) and exits non-zero if one fails.

## Files

- `Program.cs` — the four scenarios and the two graphs, top-level statements
- `Listeners.cs` — `SpanCollector` (an `ActivityListener`), `MetricCollector`
  (a `MeterListener`), and the two bad observers
- `27-run-observability.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `InMemoryCheckpointStore`) from NuGet
