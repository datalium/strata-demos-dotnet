# 16 — loop-circuit-breaker

The loop circuit breaker, STRATA-SPEC-004's `MaxNodeHops`
(`design/24-Loop-Circuit-Breaker.md`). A meeting-scheduling agent loops
`ProposeSlot` -> `CheckCalendars` -> back to `ProposeSlot` while someone is
busy, and goes on to `SendInvite` once a slot is free. The back edge is
guarded, so `.Compile()` accepts the graph. Validation is structural and
can't tell whether the guard ever turns false
([`07-graph-validation`](../07-graph-validation) shows what it does catch).
When an attendee is on leave all week, the guard never turns false and the
agent re-plans forever. In a real agent, every pass is an LLM call.
`Compile(new CompilationOptions { MaxNodeHops = 12 })` stops it.

The Rust twin is
[`strata-demos-rust/demos/16-loop-circuit-breaker`](https://github.com/datalium/strata-demos-rust/tree/main/demos/16-loop-circuit-breaker);
its README says where the two engines differ.

## What it demonstrates

- **A loop that converges never notices the limit** (scenario 1). Alice +
  Ravi is booked in 7 hops (three proposals, three checks, one invite).
  `graph.MaxNodeHops` is 12
- **A loop that doesn't converge is refused before hop 13** (scenario 2).
  Alice + Bob + Dana ends with
  `HopLimitExceeded(Node: "ProposeSlot", Hops: 12, Limit: 12)`. The refused
  node never runs. The result refines `Failed`, so code that already handles
  `Failed` catches it. The `failed` checkpoint sits at the refused node, with
  `error_kind = hop_limit`, the message in `error`, `strata.hops = 12`, and no
  `attempts` (nothing was attempted)
- **A retry isn't a hop** (scenario 3). A calendar API that fails once per
  check makes 10 handler calls, but only 7 nodes start. A limit of exactly 7
  completes. A limit of 6 trips at `SendInvite`. With a limit of N, the N-th
  node runs, and only the node that would be N+1 is refused
- **The count survives interrupts and resumes** (scenario 4). With an interrupt
  boundary on `ProposeSlot` (the organizer reviews each proposal), the thread
  freezes six times. Each checkpoint carries `strata.hops` (1, 3, 5 … 11), and
  each resume is made by a **freshly built graph**, as another process would
  make it. The thread trips at the same node, after the same 12 node starts,
  as the run without interrupts
- **`RunOptions.MaxNodeHops` replaces the limit for one run** (scenario 5):
  - resuming the tripped thread as it is trips again at once, and runs nothing
  - an operator drops Dana with a typed override
    (`HumanSignal.WithOverride("Attendees", …)`) and resumes with
    `MaxNodeHops = 20`. The count goes on from the stored 12 (hops 13-15), and
    the meeting is booked
  - a test can run the production graph with a *lower* limit (5)
  - a graph compiled with no limit gets one for a single run (4)
- **A limit below 1 is refused** by `Compile` with
  `ArgumentOutOfRangeException`

## What it does *not* cover

- **Limits inside subgraphs.** Each compiled graph counts its own hops. A
  subgraph node is one hop to its parent, and a child that trips its own limit
  fails the subgraph node fatally, with its checkpoint nested. The per-run
  override reaches the outermost graph only. See [`HopLimitTests.cs`](https://github.com/datalium/strata/blob/main/dotnet/tests/Datalium.Strata.Tests/HopLimitTests.cs) in
  `datalium/strata`. [`13-subgraph-composition`](../13-subgraph-composition) covers
  subgraphs
- **Fallbacks and parallel groups.** A fallback node is a hop of its own, and
  a parallel group is one hop however many branches it runs
- **Telemetry.** The trip is also a `HopLimitExceeded` run event, a
  `strata.graph.hop_limit_exceeded` counter and `error.type = hop_limit` on
  the run span ([`27-run-observability`](../27-run-observability) shows events and telemetry). `strata thread inspect`
  prints `Hops:`
- **Per-node visit limits, and token or cost budgets.** The breaker counts all
  node starts at a level. To bound time, use `RunOptions.Deadline` (see
  [`15-async-and-cancellation`](../15-async-and-cancellation))
- **A live LLM.** The "agent" proposes slots in a fixed order, and the
  calendars are a dictionary

## Run it

```bash
dotnet run --project demos/16-loop-circuit-breaker/16-loop-circuit-breaker.csproj
```

It finishes in well under a second, and the output is identical on every run:

```
Calendars: Ravi is busy Mon 09:00 and Mon 14:00; Dana is on leave all week.
The agent proposes, in order: Mon 09:00, Mon 14:00, Tue 10:00, Tue 15:00, Wed 11:00, then starts over.

=== Scenario 1: Alice + Ravi -- the loop converges, the limit is never reached ===
  Compiled with MaxNodeHops = 12.
  Tried: Mon 09:00 Ravi busy | Mon 14:00 Ravi busy | Tue 10:00 free
  Nodes started: 7 (ProposeSlot 3, CheckCalendars 3, SendInvite 1). Invite: 'Q4 planning' Tue 10:00 for Alice, Ravi

=== Scenario 2: Alice + Bob + Dana -- the agent re-plans until the breaker trips ===
  HopLimitExceeded: node 'ProposeSlot', hops 12, limit 12
  Message: hop limit of 12 reached: node 'ProposeSlot' would be hop 13
  It refines Failed, so existing `is Failed` handling catches it: True
  Nodes started: 12 (ProposeSlot 6, CheckCalendars 6, SendInvite 0) -- the 13th never ran.
  Checkpoint: Failed at ProposeSlot, 6 proposals in the frozen state
    metadata { error = hop limit of 12 reached: node 'ProposeSlot' would be hop 13, error_kind = hop_limit, strata.hops = 12 }

=== Scenario 3: the calendar API fails once per check -- Retry(1) on CheckCalendars ===
  MaxNodeHops = 7: completed. Handler calls: 10 (ProposeSlot 3, CheckCalendars 6, SendInvite 1); nodes started: 7.
  MaxNodeHops = 6: hop limit of 6 reached: node 'SendInvite' would be hop 7
  With a limit of N the N-th node runs; only the node that would be N+1 is refused.

=== Scenario 4: the organizer reviews every proposal -- an interrupt boundary inside the loop ===
  run       -> Interrupted after ProposeSlot (proposal 1: Mon 09:00), strata.hops = 1
  resume 1  -> Interrupted after ProposeSlot (proposal 2: Mon 14:00), strata.hops = 3
  resume 2  -> Interrupted after ProposeSlot (proposal 3: Tue 10:00), strata.hops = 5
  resume 3  -> Interrupted after ProposeSlot (proposal 4: Tue 15:00), strata.hops = 7
  resume 4  -> Interrupted after ProposeSlot (proposal 5: Wed 11:00), strata.hops = 9
  resume 5  -> Interrupted after ProposeSlot (proposal 6: Mon 09:00), strata.hops = 11
  resume 6  -> hop limit of 12 reached: node 'ProposeSlot' would be hop 13
  Nodes started across 7 runs: 12 (ProposeSlot 6, CheckCalendars 6, SendInvite 0) -- the same trip as scenario 2.

=== Scenario 5: RunOptions.MaxNodeHops -- one run's limit, higher or lower ===
  Resuming scenario 2's thread as it is: hop limit of 12 reached: node 'ProposeSlot' would be hop 13 (nodes run: 0)
  Resumed with Attendees = [Alice, Bob] and RunOptions { MaxNodeHops = 20 }:
    nodes run: 3 (hops 13-15, counted on from the stored 12). Invite: 'Offsite' Mon 14:00 for Alice, Bob
  A test runs the bookable meeting with RunOptions { MaxNodeHops = 5 }: hop limit of 5 reached: node 'CheckCalendars' would be hop 6
  A graph compiled with no limit (MaxNodeHops = null) gets one for a run: hop limit of 4 reached: node 'ProposeSlot' would be hop 5

=== Step 6: a limit below 1 is refused by Compile() ===
  Refused: ArgumentOutOfRangeException: MaxNodeHops must be at least 1 (Parameter 'options')

=== Done: the breaker stopped a loop that never converged, counted through retries and interrupts, and let one run go further. ===
```

Every claim above is checked at runtime. If one stops holding, the demo
throws and exits non-zero.

## Files

- `Program.cs` — the whole demo: the scenarios, the scheduling graph
  (`Scheduler.Build`), and a per-node call counter that tells handler calls
  (retries included) from node starts (hops)
- `16-loop-circuit-breaker.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `InMemoryCheckpointStore`) from NuGet
