# 28 — hosting

Strata in the .NET Generic Host (`design/35-Hosting-Integration.md`, package
`Datalium.Strata.Hosting`). A reminder service — `Schedule` -> `Wait` (a
five-minute timer) -> `Send` — is wired with one `services.AddStrata()` chain:
a SQLite checkpoint store from configuration, the compiled graph and a
`StrataRunner<Reminder>` to inject, a wake driver that resumes threads whose
timer is due, and two health checks. The demo drives the host with
`StartAsync` / `StopAsync`, so it exits on its own, and the container's clock
is a `FakeTimeProvider`, so a five-minute timer is due as soon as the demo
moves the clock.

Every earlier demo called the engine directly. This one shows what an
application gets from the host instead: nobody calls `WakeDueAsync` in a loop,
nobody opens the store or compiles the graph by hand, and stopping the process
doesn't cancel a run.

The Rust twin is
[`strata-demos-rust/demos/28-hosting`](https://github.com/datalium/strata-demos-rust/tree/main/demos/28-hosting). It isn't a
copy of this one: Rust's counterpart, `datalium-strata-host`, has no container,
so settings structs open the stores and a `Host` runs the same loops. Its README
says how the two differ.

## What it demonstrates

- **Registration**: `AddStrata().AddSqliteCheckpoints().AddHealthChecks()
  .AddGraph(...).AddWakeDriver<Reminder>()`. The SQLite store is registered
  once under every port it has (`ICheckpointStore`, `ICheckpointQuery`,
  `ICheckpointRetention`)
- **Configuration, layered**: `appsettings.json` sets `Strata:Wake`
  (interval 5 s, batch 10) and `Strata:Runs:Lease` (owner `reminders-1`,
  ttl 30 s); in-memory configuration adds `Strata:Sqlite:Path`, a temp file.
  Scenario 1 reads the bound options back and checks them
- **`StrataRunner<TState>`**, injected into an application class
  (`ReminderDesk`). Its runs carry the host's defaults: the container as
  `NodeContext.Services` (the `Send` node gets the `Outbox` from it), the
  container's clock, and the lease from configuration
- **The wake driver** (scenario 2). `RunAsync` returns `Interrupted` with a
  timer due at 09:05. At 09:04 the driver's rounds find nothing. Past 09:05 the
  driver resumes the thread, and the log reads `Running -> Running ->
  Interrupted -> Running -> Completed`. The graph is compiled with
  `CheckpointPolicy.EveryNode`, so the completed run leaves a checkpoint; the
  default policy writes nothing when a run completes
- **A clean stop** (scenario 3). Two threads are due. The driver wakes one
  (`remind-dave`, by the store's listing order) and its `Send` is held open.
  `StopAsync` is called and does not return while the run is in flight. Once
  the send is released, the run completes, the host stops, the other thread is
  still `Interrupted`, and no checkpoint is `Cancelled`: the driver's stop token
  tells the round to start nothing more, and nothing cancels the run. The
  driver's own log lines show the second "handled 1 thread(s)" landing after
  `StopAsync` was called. A second host on the same SQLite file then wakes the
  thread the first one left
- **Health checks** (scenarios 1 and 4). Both are healthy at start. Then the
  demo drops the store's tables behind its back to simulate an outage:
  `strata-checkpoints` turns `Unhealthy` (described by the failure's type,
  `SqliteException`, not its message, which could hold a connection string),
  `strata-drivers` turns `Degraded` once a driver round fails, and the host
  keeps running. Stopping it then ends the driver's back-off wait at once
- **The start-up check** (scenario 5). A graph with a node nobody wired in
  makes `StartAsync` throw `GraphCompilationException`
  (`STRATA_ERR_ORPHAN`) before any driver starts

## What it does *not* cover

- **Timers and waits themselves.** Calling `WakeDueAsync` by hand, event
  waits and timeouts are in [`19-waits-and-timers`](../19-waits-and-timers);
  this demo only shows the driver that calls it
- **The other drivers.** `AddRecoveryDriver<T>()` (runs whose process died;
  the engine side is in [`20-durable-execution`](../20-durable-execution)),
  `AddRetention()` (purging old threads) and `AddDecisionWorker<T>()` (Strata
  Cloud) are registered the same way and are not shown
- **`AddScheduler()`**: run admission and priority. See
  [`29-run-scheduler`](../29-run-scheduler), which uses the engine's
  `RunScheduler` directly
- **`AddInstrumentation()`** (OpenTelemetry sources and meters), and mapping
  the health checks to an HTTP endpoint. This is a console host with no web
  server
- **Postgres, Redis and Cloud stores** through the container. The stores
  themselves are in [`12-pluggable-storage`](../12-pluggable-storage)
- **A real clock.** With `TimeProvider.System` the same host waits real
  minutes. The demo moves a fake clock and polls until a background driver
  has done its work (giving up after 10 s); it never asserts how long
  anything took

## Run it

```bash
dotnet run --project demos/28-hosting/28-hosting.csproj
```

Takes a second or two. Expected output:

```
=== Scenario 1: the host starts, wired from configuration ===
  Checkpoint store: EfCoreCheckpointStore on a temp file (Strata:Sqlite:Path, in-memory configuration)
  Wake driver: every 00:00:05, batches of 10 (Strata:Wake, appsettings.json)
  Run lease: owner 'reminders-1', ttl 00:00:30 (Strata:Runs:Lease, appsettings.json)
  Health strata-checkpoints: Healthy
  Health strata-drivers: Healthy

=== Scenario 2: a five-minute timer, woken by the driver ===
  RunAsync returned Interrupted at 'Wait': a Timer due 2026-10-24T09:05:00Z
  09:04 on the clock: still waiting, nothing sent
  Past 09:05: the driver woke it. Checkpoint log: Running -> Running -> Interrupted -> Running -> Completed
  Sent: alice: stand-up at 09:05

=== Scenario 3: stopping the host with a woken run in flight ===
  Both are due; the driver woke one, and its Send is held open.
  StopAsync called. Host stopped yet? False -- it waits for the run.
  Released the send. The host stopped:
    remind-carol: Interrupted (still waiting on its timer)
    remind-dave: Completed
    sends started: 1, cancelled checkpoints: 0
  The driver's log on this host:
    [Information] Strata driver wake:Reminder started, every 00:00:05
    [Information] Strata driver wake:Reminder handled 1 thread(s)
    -- StopAsync called --
    [Information] Strata driver wake:Reminder handled 1 thread(s)
    [Information] Strata driver wake:Reminder stopped
  A second host on the same file:
    woke the one left behind and sent it: carol: deploy window

=== Scenario 4: the store breaks under the running host ===
  Health strata-checkpoints: Unhealthy (SqliteException)
  Health strata-drivers: Degraded (wake:Reminder's last round failed: SqliteException)
  The host is still running: True
  Stopped with the driver in its back-off wait, on a clock that wasn't moving.

=== Scenario 5: a graph with a structural error ===
  StartAsync threw: [STRATA_ERR_ORPHAN] Node 'Escalate' is isolated and unreachable from the entry point 'Schedule'.
  Drivers started: 0. The graph compiles at start-up, not at its first run.

=== Done: one AddStrata() chain gave the store, the runner, a wake loop, health checks and a clean stop. ===
```

The clock starts at 2026-10-24 09:00 UTC, so the times are the same on every
run. The demo checks each claim (bound options, health statuses, checkpoint
statuses, send counts, the error code) and exits non-zero if one fails. The
SQLite file is a temp file, deleted at the end.

## Files

- `Program.cs` — the whole demo: the host builder, the graph, the five
  scenarios, and three small classes (`Outbox`, `ReminderDesk`, and
  `DriverLog`, a logger provider that keeps the hosting package's log lines)
- `appsettings.json` — the `Strata:Wake` and `Strata:Runs` settings, copied
  next to the binary
- `28-hosting.csproj` — references `Datalium.Strata`,
  `Datalium.Strata.Storage` and `Datalium.Strata.Hosting` from NuGet, plus the
  `Microsoft.Extensions.Hosting` and `Microsoft.Extensions.TimeProvider.Testing`
  packages
