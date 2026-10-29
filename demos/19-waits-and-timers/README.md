# 19 — waits-and-timers

Event waits, durable timers and ending a thread
(`design/29-Event-Waits-And-Timers.md`, `design/28-Ending-A-Thread.md`,
`design/42-Waking-Through-A-Resume.md`). An online shop's order process
spends most of its life waiting: for the payment provider, for a fraud
reviewer, for the carrier. While it waits, no process holds it. Each order is
a few rows in a SQLite file, and every step of the demo opens a fresh
`EfCoreCheckpointStore` on that file, the way a webhook handler, a cron job
and a support tool would, each in its own process.

```
Place -> AwaitPayment      event "payment.received", at most 24 h, no timeout route
      -> FraudReview       orders of $1000 or more: a human boundary, at most 2 h, then Escalate
      -> Pack -> AwaitPickup (a 4 h timer) -> Ship
```

A `FakeTimeProvider` passed as `RunOptions.TimeProvider` is the clock, so a
day of waiting takes no time and every `wake_at` is exact.

The Rust twin is
[`strata-demos-rust/demos/19-waits-and-timers`](https://github.com/datalium/strata-demos-rust/tree/main/demos/19-waits-and-timers);
its README says where the two engines differ.

## What it demonstrates

- **A waiting thread is only rows.** After step 1 the store is disposed and
  the demo reads the file with plain `Microsoft.Data.Sqlite`: five
  `interrupted` rows in the `checkpoint_threads` head table, each with a
  `wake_at`, and the `wait` column
  `{"kind":"event","event_type":"payment.received","wake_at":"..."}`
- **`AddWaitForEvent(name, eventType, timeout)` answered by
  `SignalAsync(threadId, EventSignal, store)`.** The signal's overrides
  (`WithOverride("Paid", true)`, `WithOverride("PaymentRef", ...)`) replace
  top-level fields of the waiting state, so the payload is in the state for
  every later node
- **The edge out of a wait is chosen when it freezes**, on the state as it was
  then. The $2400 order's `NextNode` is already `FraudReview` before anyone
  pays, so a signal's payload can't change where the thread goes next
- **The wrong answer is refused, and writes nothing** (5 rows before and
  after): a human `ResumeAsync` of an event wait, a signal of another event
  type, and `LevelSignals.Due` before the wait is due
- **A human boundary with a timeout**:
  `AddInterruptBoundary("FraudReview", onReject: "Refund", timeout: 2 h, onTimeout: "Escalate")`.
  Nobody answers it, so at 12:00 `WakeDueAsync` takes the `onTimeout` route
  (`WakeReason.Timeout`), and the thread records no decision
- **`AddTimer(name, delay)` woken by `WakeDueAsync`** once due, and not
  before: at 12:00 only the fraud review is due, at 13:30 both pickups are
- **`ThreadQuery.DueBy`**: the application lists due threads through the
  query port, from the head table's `wake_at` column
- **`LevelSignals.Due` through your own `ResumeStreaming`**: the application
  wakes `order-1005` itself and gets the woken run's chunks (`WakeDueAsync`
  doesn't stream). The resume decides at its own load that the thread is
  still due. The driver that runs next finds only `order-1001`
- **A timeout with no route ends the thread**: nobody pays for `order-1002`,
  so the next morning `WakeDueAsync` ends it, `Cancelled(Ended)`, with
  `strata.end_reason` = `timed_out`, which a `ThreadQuery.Metadata` filter
  finds
- **`EndAsync(threadId, ThreadEnd.By("customer"), store)`** ends a waiting
  thread on request: `strata.end_reason` = `requested`, `strata.ended_by` =
  `customer`, no `wait` left, and a late payment signal is refused
- **What the file holds at the end**: a completed run writes nothing (the
  graph uses the default `CheckpointPolicy.Boundaries`), so a finished order's
  head is its last resume stamp, `resumed`. The stamp keeps the `wake_at` of
  the wait it answered; only an interrupted, unresumed thread is due

## What it does *not* cover

- **The hosted wake driver** (`AddWakeDriver<TState>()`,
  `design/35-Hosting-Integration.md`), which calls `WakeDueAsync` on a
  cadence; see [`28-hosting`](../28-hosting). Here the demo calls it by hand
  at chosen times
- **Graph identity**, which makes `WakeDueAsync` list only its own graph's
  threads. This file holds one graph's threads, so it doesn't matter here;
  see [`22-graph-identity`](../22-graph-identity)
- **Two drivers racing** for one due thread (the conditional save makes one
  win and the other skip it), and waits inside a subgraph. For leases, see
  [`21-thread-leases`](../21-thread-leases)
- **`AddTimer(name, state => wakeAt)`**, a timer due at a time computed from
  the state, and `LevelSignals.Event(signal)`, which streams a signalled run
- **A signal that arrives before its wait** is refused, not buffered. The
  engine doesn't build that

## Run it

```bash
dotnet run --project demos/19-waits-and-timers/19-waits-and-timers.csproj
```

Expected output (the clock is fake, so every time printed is the same on
every run):

```
=== Step 1: five orders are placed and wait for payment ===
  order-1001 ($80): waits for event 'payment.received' until 2026-11-03T09:00:00Z
  order-1002 ($45): waits for event 'payment.received' until 2026-11-03T09:00:00Z
  order-1003 ($2400): waits for event 'payment.received' until 2026-11-03T09:00:00Z
  order-1004 ($60): waits for event 'payment.received' until 2026-11-03T09:00:00Z
  order-1005 ($35): waits for event 'payment.received' until 2026-11-03T09:00:00Z
  order-1003's next node, fixed at the freeze: FraudReview
  The store is disposed. What is left of the five orders:
    checkpoint_threads (the head table the query port reads):
      order-1001  interrupted  wake_at 2026-11-03T09:00:00Z
      order-1002  interrupted  wake_at 2026-11-03T09:00:00Z
      order-1003  interrupted  wake_at 2026-11-03T09:00:00Z
      order-1004  interrupted  wake_at 2026-11-03T09:00:00Z
      order-1005  interrupted  wake_at 2026-11-03T09:00:00Z
  order-1001's wait column: {"kind":"event","event_type":"payment.received","wake_at":"2026-11-03T09:00:00Z"}

=== Step 2: the wrong answers to a payment wait are refused ===
  A clerk approves it:      thread 'order-1001' is waiting for event 'payment.received' at [AwaitPayment]: only a signal of that event answers it
  A refund.issued event:    thread 'order-1001' is not waiting for event 'refund.issued': it is waiting for event 'payment.received' at [AwaitPayment]
  A wake before it is due:  thread 'order-1001' is not due until 2026-11-03T09:00:00Z: it is waiting for event 'payment.received' at [AwaitPayment]
  Checkpoint rows before and after: 5 and 5.

=== Step 3: 2026-11-02 09:20, payments arrive for order-1001, order-1003 and order-1005 ===
  order-1001: paid (PAY-5531), now waits for a timer until 2026-11-02T13:20:00Z
  order-1003: paid (PAY-5532), now waits for a human until 2026-11-02T11:20:00Z
  order-1005: paid (PAY-5533), now waits for a timer until 2026-11-02T13:20:00Z

=== Step 4: 2026-11-02 09:30, the customer cancels order-1004 before paying ===
  Ended at AwaitPayment (Ended): status Ended, strata.end_reason=requested, strata.ended_by=customer
  A late payment for it:    thread 'order-1004' was ended: an ended thread can't be resumed

=== Step 5: 2026-11-02 12:00, the wake driver runs ===
  Due now (ThreadQuery.DueBy): order-1003
  Woke order-1003 (Timeout): completed, placed for $2400 / held for a fraud review / not reviewed within 2 h: escalated to the risk team

=== Step 6: 2026-11-02 13:30, the carrier is here: both pickup timers are due ===
  Due now: order-1001, order-1005
  The application wakes order-1005 through its own ResumeStreaming(LevelSignals.Due):
    chunk NodeStart Ship
    chunk NodeEnd   Ship
    -> completed, placed for $35 / packed, payment PAY-5533 / handed to the carrier
  The wake driver woke order-1001 (Timer): completed, placed for $80 / packed, payment PAY-5531 / handed to the carrier

=== Step 7: 2026-11-03 09:00, order-1002 was never paid ===
  Woke order-1002 (Timeout): cancelled (Ended) at AwaitPayment
  Threads with strata.end_reason=timed_out: order-1002

=== Done: what the file holds now ===
    checkpoint_threads (the head table the query port reads):
      order-1001  resumed      wake_at 2026-11-02T13:20:00Z
      order-1002  ended        wake_at -
      order-1003  resumed      wake_at 2026-11-02T11:20:00Z
      order-1004  ended        wake_at -
      order-1005  resumed      wake_at 2026-11-02T13:20:00Z
  16 checkpoint rows in all. A completed run writes nothing, so a finished order's head is its last resume stamp.
  The stamp keeps the wake_at of the wait it answered; only an interrupted, unresumed thread is due.
```

The demo checks each claim as it goes (the waits and their `wake_at`, the
refusals, that a refusal writes nothing, which threads are due and why each
woke, the end reasons, the final rows) and exits non-zero if one fails. The
database is `bin/<config>/net10.0/orders.db`, deleted at the start of each
run.

## Files

- `Program.cs` — the whole demo, top-level statements in a single file
- `19-waits-and-timers.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `EfCoreCheckpointStore`) from NuGet, and
  `Microsoft.Extensions.TimeProvider.Testing` for `FakeTimeProvider`
