# 10 — parallel-fan-out

Parallel fan-out / fan-in (`design/14-Parallel-Execution.md`): a trip planner
(`ParseRequest` -> `GatherInfo` -> `BuildItinerary`) whose `GatherInfo` node is
a **parallel group**. It asks three simulated services at once — flights,
hotels, weather — and a reducer merges what they return. To the rest of the
graph the group is one ordinary node, with ordinary edges in and out.

The services take different amounts of time (flights 300 ms, hotels 200 ms,
weather 100 ms) so the demo can watch what a parallel group actually
promises: real overlap, a merge order that doesn't depend on timing, and
failures handled at the level of the group.

`01`-`09` were strictly sequential — the executor followed one edge at a time.
This is the first demo where two things run at once. It covers the basic
group, with no options; [`17-parallel-joins`](../17-parallel-joins) covers
what `ParallelGroupOptions` adds (`design/26-Parallel-Group-Controls-And-Joins.md`).

## What it demonstrates

- `.AddParallelGroup(name, branches, reducer)` with a `ParallelBranches<TState>`
  builder. Each branch here is **async**, `Func<TState, NodeContext,
  CancellationToken, ValueTask<TState>>` (`design/17-Async-Execution.md`): it
  receives the state as it was when the group started and returns its own copy
  (`state with { ... }`), so branches can't see each other. Unlike Rust's
  ownership, that's a convention — a branch must build a new state rather than
  mutate the one it was handed
- **Real concurrency**: an async branch runs as a task and holds no thread
  while it waits (a sync `Branch(name, Func<TState, TState>)` gets a thread of
  its own). Scenario 1 prints the peak number of branches in flight (3 of 3)
  and the wall time (~300 ms, the slowest branch — not the ~600 ms sum; the
  first run also pays for JIT)
- **A deterministic merge**: the services *finish* in the order weather,
  hotels, flights, but the reducer receives their results in **declaration
  order** (flights, hotels, weather), so the merged log is identical on every
  run. The reducer also gets the entry state, so each branch's contribution can
  be a delta (here: the log lines added after the entry log)
- **Failure is handled at the group level** — a branch fails by throwing
  `NodeException.Transient(...)` / `NodeException.Fatal(...)`. **The first
  failure fails the group at once and cancels the siblings still running**:
  their token fires, and here their `Task.Delay(latency, ct)` throws. The group
  is a fallible node, so it reuses `RecoveryPolicy` from
  [`08-error-recovery`](../08-error-recovery):
  - **Scenario 2, a transient blip**: hotels times out once, which cancels the
    flights call in flight. `Retry(2)` re-runs **all three** branches (6 service
    calls, not 4) — branches must be safe to repeat — and the failed attempt's
    log lines are discarded
  - **Scenario 3, a fatal error + fallback**: hotels has no rooms. Weather had
    already answered and flights is cancelled; what finished is thrown away
    with the failed group, and `UseCachedPlan` runs on the state as it was
    *before* the group. A fatal error skips all 3 retries
  - **Scenario 4, a fatal error, no fallback**: the `NodeFailed` message names
    the failing branch (`branch 'hotels': no rooms left on these dates`), and
    not the cancelled one
- **Scenario 5, streaming branches**: the same branches added with
  `.StreamingBranch(...)` and run under `RunStreaming` push tokens through
  `NodeContext.Stream`, concurrently on the run's one bounded channel. Each
  token arrives named `GatherInfo/<branch>`, one branch's tokens stay in order,
  and only the group gets `NodeStart`/`NodeEnd` chunks — branches aren't nodes
- **Compile-time check**: a group with two branches of the same name (or none)
  is rejected by `.Compile()` with an `InvalidOperationException`

## What it does *not* cover

- **Group options**: a concurrency limit (`MaxConcurrency`), a timeout per
  branch (`BranchTimeout`), letting siblings finish after a failure
  (`CancelSiblingsOnFailure = false`), keeping partial results
  (`PartialResults.KeepSucceeded`), and joins other than "every branch"
  (`FirstSuccess`, `FirstMatching`, `Quorum`). See
  [`17-parallel-joins`](../17-parallel-joins)
- **Fan-out over a list decided at runtime**: a group's branch set is fixed
  when the graph is built. `AddMap` runs a child graph per item; see
  [`18-map-fan-out`](../18-map-fan-out)
- **Branches are closures, not graph nodes.** They have no edges of their
  own, no per-branch recovery policy, and no interrupt boundaries. The graph
  definition lists them by name (see demo 14), but fan-out written as several
  outgoing `.AddEdge(...)`s from one node isn't supported. A branch can't hold
  a group
- **A sync branch being cancelled.** It can't see its token: when the group
  gives up on it, it runs on and its result is discarded. The demo uses async
  branches so the cancellation is visible
- **No live service.** The services are a `Task.Delay` and a canned answer, so
  the demo runs offline. The millisecond figures are measured and vary a little
  from run to run; the ordering and the counts don't

## Run it

```bash
dotnet run --project demos/10-parallel-fan-out/10-parallel-fan-out.csproj
```

Takes about 2 seconds (the scenarios really wait). Expected output (the
`[ N ms]` figures will differ slightly; the first scenario's include JIT
warm-up):

```
=== Scenario 1: all three services healthy ===
    [  55 ms] flights call 1: started
    [  59 ms] hotels call 1: started
    [  59 ms] weather call 1: started
    [ 169 ms] weather call 1: answered
    [ 262 ms] hotels call 1: answered
    [ 369 ms] flights call 1: answered
  Completed in 360 ms.
  Sequentially the three calls would take about 600 ms (300 + 200 + 100).
  Peak branches in flight: 3 of 3 -- they really overlapped.
  Completion order:  weather, hotels, flights
  Merged log (declaration order, identical on every run):
    - request: trip to Lisbon
    - flights: LH 401 for $612
    - hotels: Hotel Aurora at $148/night
    - weather: sunny, 24C
  Summary: Lisbon: LH 401, $612 | Hotel Aurora, $148/night | sunny, 24C

=== Scenario 2: hotels times out once -- Retry(2) on the group ===
    [   0 ms] flights call 1: started
    [   0 ms] hotels call 1: started
    [   0 ms] weather call 1: started
    [ 100 ms] weather call 1: answered
    [ 208 ms] hotels call 1: transient error: hotels timed out
    [ 210 ms] flights call 1: cancelled (the group gave up on it)
    [ 213 ms] flights call 2: started
    [ 213 ms] hotels call 2: started
    [ 213 ms] weather call 2: started
    [ 318 ms] weather call 2: answered
    [ 426 ms] hotels call 2: answered
    [ 519 ms] flights call 2: answered
  Completed. Service calls -- flights: 2, hotels: 2, weather: 2.
  The failure cancelled flights mid-call (1 cancelled), and the retry re-ran *all three*: 6 calls in total, not the 4 a hotels-only retry would make. Branches must be safe to repeat.
  Summary: Porto: LH 401, $612 | Hotel Aurora, $148/night | sunny, 24C

=== Scenario 3: hotels has no rooms -- Retry(3), fallback 'UseCachedPlan' ===
    [   0 ms] flights call 1: started
    [   0 ms] hotels call 1: started
    [   0 ms] weather call 1: started
    [ 107 ms] weather call 1: answered
    [ 200 ms] hotels call 1: fatal error: no rooms left on these dates
    [ 201 ms] flights call 1: cancelled (the group gave up on it)
  Fell back after 3 service call(s) in total -- a fatal error skips all 3 retries.
  Weather had already answered; flights was cancelled mid-call (1 cancelled). What finished is discarded with the failed group:
    Flight: null, Hotel: null, Forecast: null
  Log: ["request: trip to Madeira"]
  Summary: Madeira: cached plan (live lookup unavailable)

=== Scenario 4: same failure, no fallback -- the error names the branch ===
    [   0 ms] flights call 1: started
    [   0 ms] hotels call 1: started
    [   0 ms] weather call 1: started
    [ 108 ms] weather call 1: answered
    [ 200 ms] hotels call 1: fatal error: no rooms left on these dates
    [ 201 ms] flights call 1: cancelled (the group gave up on it)
  Failed: group 'GatherInfo' gave up after 1 attempt(s) -- branch 'hotels': no rooms left on these dates
  The cancelled flights call is not named: it didn't fail, it was stopped.

=== Scenario 5: streaming branches under RunStreaming ===
    [   3 ms] flights call 1: started
    [   3 ms] hotels call 1: started
    [   3 ms] weather call 1: started
    [ 112 ms] weather call 1: answered
    [ 204 ms] hotels call 1: answered
    [ 312 ms] flights call 1: answered
  Tokens, grouped by the node name they arrived with (each branch's own stay in order):
    GatherInfo/flights asking... | flights: LH 401 for $612
    GatherInfo/hotels  asking... | hotels: Hotel Aurora at $148/night
    GatherInfo/weather asking... | weather: sunny, 24C
  NodeStart chunks: ParseRequest, GatherInfo, BuildItinerary -- branches are not nodes and get none.
  Summary: Faro: LH 401, $612 | Hotel Aurora, $148/night | sunny, 24C

=== Step 6: a malformed group is rejected at .Compile(), not at runtime ===
  Rejected: parallel group 'GatherInfo' has more than one branch named 'flights'

=== Done: three services ran at once, merged in a fixed order, a failing branch was handled at the group level, and branches streamed. ===
```

The demo asserts the parts that must not vary (peak in flight, completion
order, merged log, call and cancellation counts, the failure message, the
streamed tokens' attribution) and throws if they change.

## Files

- `Program.cs` — the whole demo, top-level statements in a single file
- `10-parallel-fan-out.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `InMemoryCheckpointStore`) from NuGet
