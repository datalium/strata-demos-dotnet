# 21 — thread-leases

Thread leases with fencing (`design/32-Thread-Leases.md`, SG-12). Two workers
get the same invoice, as happens when a queue redelivers a message whose first
consumer went quiet. Each runs with
`RunOptions.Lease = new LeaseOptions(owner, ttl)`. The lease is kept in the
checkpoint store, and every checkpoint is saved under it. Its token is a
per-thread counter, and a save is written only if the token is still the
thread's latest. So one worker runs the thread at a time, and a worker that
lost its lease can't write over the new owner's progress.

The graph is `Draft` (an interrupt boundary, so the run freezes for approval
after it) -> `Send`. Each worker stamps the draft with its name, which shows
whose work the thread holds.

The Rust twin is
[`strata-demos-rust/demos/21-thread-leases`](https://github.com/datalium/strata-demos-rust/tree/main/demos/21-thread-leases);
its README says where the two engines differ.

## What it demonstrates

- **Scenario 1, a held thread.** While `worker-a` is in `Draft`, `worker-b` is
  refused: `Failed("thread 'invoice-1' is held by 'worker-a' until 2026-10-24T12:00:30Z")`.
  It runs no node, writes nothing, and its events have no `LeaseAcquired`.
  `Draft` then takes 40 seconds, longer than the 30-second ttl, but `worker-a`
  renews every ttl / 3, so its lease now runs until 12:01:10 and `worker-b` is
  still refused. When `worker-a`'s run returns, it releases the lease
- **Scenario 2, no leases.** `worker-a` stalls, `worker-b` drafts and freezes
  the invoice, then `worker-a` wakes up and saves its stale draft on top. The
  thread now waits on `worker-a`'s draft, and `worker-b`'s work is buried
- **Scenario 3, a fenced save.** The same stall with leases. `worker-a`'s lease
  expires on the store's clock, `worker-b` takes the thread (token 2) and
  freezes it. When `worker-a` finishes `Draft`, its save under token 1 is
  refused. Its run ends `Cancelled` with `CancelReason.LeaseLost` and writes
  nothing, not even a `cancelled` checkpoint. `worker-b` had already released
  its lease by then, so nobody held the thread: the token alone decided
- **Scenario 4, a refused renewal.** `worker-a`'s next renewal finds the
  takeover. Its run is cancelled straight away (`Draft` unfinished), and
  `Draft`'s `CancellationToken` fires, so the worker stops working on a thread
  that isn't its
- **The run events**: `LeaseAcquired(owner, token, expiresAt)` before the
  first save, and `LeaseLost(owner, token)` followed by
  `RunCompleted(Cancelled, LeaseLost)`, with no `CheckpointSaved` from the
  stale worker

The demo checks each of these at runtime and exits non-zero if one fails.

**Fake clocks, no waiting.** The store takes a `TimeProvider` and judges
expiry by it (in Postgres the server's clock does this). Each worker's run
has its own clock (`RunOptions.TimeProvider`), which drives its renewals. A
stalled worker is one whose clock the demo doesn't move: a GC pause or a
frozen VM fires no timers. In scenario 1 the store and `worker-a` share a
clock, so `worker-a` renews as time passes.

## What it does *not* cover

- **Crash recovery**, where a lease tells a dead worker's thread from a live
  one: see [`20-durable-execution`](../20-durable-execution), which also shows
  that under `CheckpointPolicy.EveryNode` a stale worker's `completed`
  checkpoint is fenced too. Under the default policy a run that completes
  writes nothing, so there is nothing to fence
- **Resume, signal, wake and end under a lease.** They acquire the same way,
  after their checks, so a refused resume never takes the lease. The wake
  driver skips a held thread
- **Renewals failing for less than a ttl** (one failed renewal isn't a loss),
  the SQL `checkpoint_leases` table, the Redis lease keys, and the telemetry
  counters. See [`ThreadLeaseTests.cs`](https://github.com/datalium/strata/blob/main/dotnet/tests/Datalium.Strata.Tests/ThreadLeaseTests.cs) in `datalium/strata`
- **Leases are opt-in.** Saves without a lease aren't refused while a lease is
  live, so every worker on a thread uses leases, or none is protected

## Run it

```bash
dotnet run --project demos/21-thread-leases/21-thread-leases.csproj
```

It runs in about a second. Every time comes from a fake clock starting at
2026-10-24 12:00:00 UTC, so the output is the same on every machine:

```
=== Scenario 1: worker-a holds the thread, worker-b is refused ===
  worker-a is in Draft. Lease: 'worker-a', token 1, until 2026-10-24T12:00:30Z.
  worker-b -> Failed: thread 'invoice-1' is held by 'worker-a' until 2026-10-24T12:00:30Z
  worker-b's events: RunStarted, RunCompleted(Failed). It drafted nothing and wrote nothing.
  40 s later, still in Draft: worker-a renewed every 10 s. Lease: token 1, until 2026-10-24T12:01:10Z.
  worker-b again -> Failed: thread 'invoice-1' is held by 'worker-a' until 2026-10-24T12:01:10Z
  worker-a froze at Draft for approval. Latest checkpoint: [interrupted at Draft, drafted by worker-a].
  worker-a's events: RunStarted, LeaseAcquired('worker-a', token 1, until 2026-10-24T12:00:30Z), NodeStarted, NodeCompleted(Committed), CheckpointSaved, Interrupted, RunCompleted(Interrupted).
  Lease after the run: none. A lease lasts as long as one run, not as long as the thread.

=== Scenario 2: no leases -- the stale worker overwrites the new one ===
  worker-a stalls in Draft. The queue gives the invoice to worker-b.
  worker-b drafts and freezes. Latest checkpoint: [interrupted at Draft, drafted by worker-b].
  worker-a wakes up and freezes too. Latest checkpoint: [interrupted at Draft, drafted by worker-a].
  Two checkpoints, and the latest is worker-a's: an approval now sends the stale draft.

=== Scenario 3: leases -- worker-a's lease is taken over, and its save is refused ===
  worker-a takes the lease (token 1, until 12:00:30) and stalls in Draft.
  At 12:00:31 the lease has expired. worker-b takes the thread and freezes it. Latest checkpoint: [interrupted at Draft, drafted by worker-b].
  worker-b's events: RunStarted, LeaseAcquired('worker-b', token 2, until 2026-10-24T12:01:01Z), NodeStarted, NodeCompleted(Committed), CheckpointSaved, Interrupted, RunCompleted(Interrupted).
  worker-a wakes up and finishes Draft -> Cancelled (LeaseLost) at 'Draft'.
  worker-a's events: RunStarted, LeaseAcquired('worker-a', token 1, until 2026-10-24T12:00:30Z), NodeStarted, NodeCompleted(Committed), LeaseLost('worker-a', token 1), RunCompleted(Cancelled, LeaseLost).
  The log still holds one checkpoint, worker-b's: [interrupted at Draft, drafted by worker-b].
  worker-b had already released its lease, so nobody held the thread: the save was refused
  because token 1 is no longer the thread's latest. Expiry only lets someone else in; the token decides.

=== Scenario 4: leases -- worker-a finds out at its next renewal ===
  worker-a's lease expired while it waited in Draft; worker-b took the thread. Latest checkpoint: [interrupted at Draft, drafted by worker-b].
  worker-a's renewal at +10 s is refused -> Cancelled (LeaseLost) at 'Draft', Draft unfinished.
  Draft's CancellationToken fired, so worker-a stopped working on a thread that isn't its.
  worker-a's events: RunStarted, LeaseAcquired('worker-a', token 1, until 2026-10-24T12:00:30Z), NodeStarted, LeaseLost('worker-a', token 1), NodeCompleted(Cancelled), RunCompleted(Cancelled, LeaseLost).
  Still one checkpoint, worker-b's: [interrupted at Draft, drafted by worker-b].

=== Done: one thread, one owner at a time, and a stale owner writes nothing. ===
```

In scenario 3 `Draft` committed (`NodeCompleted(Committed)`) and the save
after it was refused. In scenario 4 the engine gave up on `Draft` before it
returned (`NodeCompleted(Cancelled)`).

## Files

- `Program.cs`: the whole demo, top-level statements in a single file
- `21-thread-leases.csproj`: references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `InMemoryCheckpointStore`, which takes a
  `TimeProvider`) from NuGet, and `Microsoft.Extensions.TimeProvider.Testing` for
  `FakeTimeProvider`
