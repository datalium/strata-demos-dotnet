# 29 — run-scheduler

The run scheduler (`design/40-Run-Scheduler.md`): a `RunScheduler`, given to a
run through `RunOptions.Scheduler`, admits runs to a fixed number of slots.
A run waits for a slot before it loads or writes anything, and gives it back
when `RunAsync` returns. Four scenarios, one rule each: strict priority, a
reserved slot, per-tenant limits, and a bounded queue that sheds.

Every run here has one node that records that it started and then waits on a
`TaskCompletionSource` the demo releases. So which runs hold slots at any
moment is the demo's choice, not a matter of timing, and the order of
admission is read from the order the nodes started. The runs' clock is a
`FakeTimeProvider`, so how long a run queued (`RunStarted.QueuedFor`,
`NotAdmitted.Waited`) is an exact figure.

This is plain engine code: no host. The Rust twin is
[`strata-demos-rust/demos/29-run-scheduler`](https://github.com/datalium/strata-demos-rust/tree/main/demos/29-run-scheduler);
its README says where the two engines differ.

## What it demonstrates

- **Strict priority, first come first served within one** (scenario 1). One
  slot, held by `blocker`. Five runs arrive: low-1 (p0), high-1 (p5),
  low-2 (p0), high-2 (p5), mid-1 (p2). As each slot holder finishes, the next
  to start is high-1, high-2, mid-1, low-1, low-2. The clock moves a minute
  per hand-over, so `RunStarted.QueuedFor` reads 1 to 5 minutes
- **A reserved slot** (scenario 2). Three slots,
  `Reservations = [new SlotReservation(MinPriority: 5, Slots: 1)]`. Two p0 runs
  start; a third waits beside the free slot, which is the reserve. A p5 run
  arrives and takes it at once. When the p5 run finishes, its slot goes back
  to the reserve and the p0 run still waits: a reservation is headroom that
  lends nothing back. The p0 run starts when an unreserved slot frees up
- **Tenant limits** (scenario 3). Four slots, `TenantLimit = 1`,
  `TenantLimits = { ["initech"] = 0 }`. The tenant is the run's
  `RunOptions.Metadata["tenant"]` (the key is `RunSchedulerOptions.TenantKey`).
  acme-2 waits behind acme-1 with slots free, and globex-1, which arrived after
  it, starts at once: a tenant at its limit holds back only its own runs.
  initech is paused (limit 0). `Snapshot().Tenants` shows each tenant's active,
  queued and limit. `SetTenantLimit` changes a limit while runs wait, and the
  waiting run starts at once
- **A bounded queue that sheds** (scenario 4). One slot, `MaxQueued = 2`, two
  p0 runs waiting. A third p0 run outranks nobody, so it is refused on arrival:
  `NotAdmitted(QueueFull)`, waited 0. Thirty seconds later a p5 run arrives
  and sheds the lowest-ranked waiting run (lowest priority, latest arrival):
  report-b returns `NotAdmitted(Shed)` after 30 s. Both refused runs leave no
  checkpoint and no run event, while report-a, which ran, leaves both. The
  graph uses `CheckpointPolicy.EveryNode` so that contrast is real: under the
  default policy a completed run writes nothing either
- **`Snapshot()`**: active and queued counts against `MaxConcurrent` and
  `MaxQueued`, printed after each step

## What it does *not* cover

- **The other ways a wait ends**: `MaxQueueTime` (`QueueTimeout`), the run's
  deadline (`Deadline`), its token (`Cancelled`) and `Close()` (`Closed`).
  They return the same `NotAdmitted` with their own reason
- **`TenantMaxQueued`**, a per-tenant cap on waiting runs
- **Drivers under a scheduler.** The wake and recovery drivers wait for a slot
  per thread and skip a thread that isn't admitted; their options take a
  `Priority`
- **Hosting.** In a host, `services.AddStrata().AddScheduler()` registers one
  `RunScheduler` from `Strata:Scheduler` (`MaxConcurrent`, `MaxQueued`,
  `Reservations`, `TenantLimits`, ...), every `StrataRunner<TState>` run and
  every driver's run uses it, `CreateOptions(priority: ...)` sets the priority,
  and the host closes the scheduler when it begins to stop, so a queued run is
  refused instead of holding up the shutdown. The host itself is in
  [`28-hosting`](../28-hosting)
- **Telemetry**: the `strata.scheduler.*` metrics and the run span's
  `strata.scheduler.wait_ms`
- **Preemption, aging, rate limits, and changing `MaxConcurrent` while
  running.** The scheduler has none of these (design/40, "Not built")

## Run it

```bash
dotnet run --project demos/29-run-scheduler/29-run-scheduler.csproj
```

Takes under a second. Expected output:

```
=== Scenario 1: one slot, five runs waiting at three priorities ===
  Arrived in this order: low-1 (p0), high-1 (p5), low-2 (p0), high-2 (p5), mid-1 (p2)
  active 1 of 1, queued 5 of 1024
  Started in this order: high-1, high-2, mid-1, low-1, low-2
    high-1 RunStarted.QueuedFor = 00:01:00
    high-2 RunStarted.QueuedFor = 00:02:00
    mid-1  RunStarted.QueuedFor = 00:03:00
    low-1  RunStarted.QueuedFor = 00:04:00
    low-2  RunStarted.QueuedFor = 00:05:00

=== Scenario 2: three slots, one kept for priority 5 and up ===
  batch-1..3 (p0): active 2 of 3, queued 1 of 1024
  batch-3 waits beside a free slot: that slot is the reserve.
  urgent (p5) arrives and starts at once: active 3 of 3, queued 1 of 1024
  urgent finishes; its slot goes back to the reserve: active 2 of 3, queued 1 of 1024
  batch-1 finishes; batch-3 starts: active 2 of 3, queued 0 of 1024

=== Scenario 3: four slots, one run per tenant, initech paused ===
  Arrived: acme-1, acme-2, globex-1, initech-1
  active 2 of 4, queued 2 of 1024
    acme     active 1, queued 1, limit 1
    globex   active 1, queued 0, limit 1
    initech  active 0, queued 1, limit 0
  SetTenantLimit("initech", 1): initech-1 starts. active 3 of 4, queued 1 of 1024
  SetTenantLimit("acme", 2): acme-2 starts. active 4 of 4, queued 0 of 1024

=== Scenario 4: one slot, room for two to wait ===
  blocker running, report-a and report-b (p0) waiting: active 1 of 1, queued 2 of 2
  report-c (p0) arrives: NotAdmitted(QueueFull) after 00:00:00 -- "run not admitted: the scheduler's queue is full"
  page-oncall (p5) arrives 30 s later: report-b NotAdmitted(Shed) after 00:00:30 -- "run not admitted: shed from the scheduler's queue for a higher-priority run"
  active 1 of 1, queued 2 of 2
  Started after blocker: page-oncall, report-a
    report-a: 2 checkpoint(s), 6 run event(s)
    report-b: 0 checkpoint(s), 0 run event(s)
    report-c: 0 checkpoint(s), 0 run event(s)

=== Done: priority, a reserve, tenant limits and shedding, each decided before a run touched the store. ===
```

The times are fake-clock time, the same on every run. The demo checks each
claim (start order, snapshot counts, per-tenant usage, refusal reasons and
waits, what the refused runs left) and exits non-zero if one fails. Run events
are delivered off the run's path, so before it counts them the demo waits for
the `RunCompleted` of every run that was admitted.

## Files

- `Program.cs` — the whole demo: the four scenarios, and `Lab`, which holds a
  scenario's scheduler, graph, store, clock and per-run gates
- `29-run-scheduler.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet, plus `Microsoft.Extensions.TimeProvider.Testing` for
  the fake clock
