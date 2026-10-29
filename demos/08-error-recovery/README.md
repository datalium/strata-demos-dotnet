# 08 — error-recovery

Error recovery ([`design/04-Error-Recovery.md`](https://github.com/datalium/strata/blob/main/design/04-Error-Recovery.md) and
[`design/25-Retry-Backoff.md`](https://github.com/datalium/strata/blob/main/design/25-Retry-Backoff.md) in `datalium/strata`): an order pipeline
(`ValidateOrder` -> `ChargePayment` -> `SendConfirmation`) whose
`ChargePayment` node talks to a flaky simulated payment gateway. The node is
*fallible*: it signals failure by throwing a `NodeException`, and a
`RecoveryPolicy` tells the engine what to do about it. Wait and retry a
transient failure, fall back to another node once retries run out, or persist
a `Failed` checkpoint when there is nowhere to fall back to. A `Failed`
checkpoint is not the end: once the cause is fixed, the thread is resumed and
re-enters the node that failed.

Every attempt of `ChargePayment` first **reserves a receipt number** on the
state it is building and only then calls the gateway. That reservation is the
partial work the demo watches: a failed attempt must not leak it into the
committed state, the fallback node, or the persisted checkpoint.

## What it demonstrates

- `.AddFallibleNode(name, state => ...)`: same handler shape as `AddNode`,
  but a thrown `NodeException` goes to the node's `RecoveryPolicy` instead of
  escaping. The engine keeps the last committed state and never takes a
  failed attempt's return value, so rollback is structural (it relies on
  `OrderState` being an immutable record)
- `NodeException.Transient(...)` vs `NodeException.Fatal(...)`: a timeout or a
  503 is worth retrying; a declined card isn't
- **Backoff.** `RecoveryPolicy.Retry(3).WithBackoff(200 ms, 2 s, BackoffJitter.None)`
  waits `initial × 2^(n−1)`, capped at `max`, before the n-th retry.
  Scenario 1's two timeouts wait 200 ms, then 400 ms. The default jitter is
  `Full` (a random wait in `[0, d]`); the demo turns it off so the numbers are
  stable
- **A retry hint.** `NodeException.Transient("gateway unavailable (503)", TimeSpan.FromSeconds(30))`
  carries a `Retry-After`. With a backoff set, the hint replaces the computed
  delay, capped at `max` and not jittered: scenario 2 waits 2 s, twice
- **The fallback knows why.** Once retries run out, the engine routes to
  `QueueForManualReview`, which runs on the state from *before*
  `ChargePayment` (`ReceiptId` is null, `SendConfirmation` never ran). Its
  `NodeContext.LastFailure` is a `NodeFailure` with the failed node, the kind,
  the final message, the attempts and the hint, which it writes into
  `ReviewNote`. Only a fallback's context carries it
- **A fatal error skips retries and waits.** Scenario 3 makes one gateway
  call, schedules no retry, persists a `CheckpointStatus.Failed` checkpoint
  and returns `GraphExecutionResult.NodeFailed` (a refinement of `Failed`)
- **A durable `Failed` checkpoint.** `EfCoreCheckpointStore` writes it to a
  real SQLite file and a second, independent store instance reads it back:
  the last committed state (after `ValidateOrder`), the failed node as both
  `SuspendedNode` and `NextNode`, and `error` / `error_kind` / `attempts` in
  `Metadata`. It is the shape the Rust runtime writes too
  ([`schema/fixtures/checkpoint-lifecycle/failed.json`](https://github.com/datalium/strata/blob/main/schema/fixtures/checkpoint-lifecycle/failed.json) in `datalium/strata`)
- **Resuming a `Failed` checkpoint** (`design/16-Nested-Interrupts.md`,
  "Resuming a failed thread"). The customer updates their card, and
  `ResumeAsync("order-1003", HumanSignal.Approve("support@shop.example").WithOverride("CardToken", ...), store)`
  re-enters `ChargePayment` with the committed state plus the new card.
  `ValidateOrder` does not run again, `SendConfirmation` does. A checkpoint is
  resumed once: a second resume is refused
- **Compile-time checks.** `QueueForManualReview` has no ordinary edge into
  it, yet `.Compile()` doesn't flag it as an orphan (STRATA-SPEC-004): the
  fallback route counts as a guarded edge. A policy naming a fallback that
  doesn't exist (a typo, in step 6) is rejected by `.Compile()`
- **A fake clock.** The waits run on the run's clock, `RunOptions.TimeProvider`.
  The demo passes a `FakeTimeProvider` and moves it forward, so 4 seconds of
  backoff take milliseconds. The delays it prints come from the engine's
  `RetryScheduled` run events, not from timing anything

## What it does *not* cover

- **Node timeouts** (`SetTimeout`, a slow attempt becoming a transient
  failure) and cancelling a run: see
  [`15-async-and-cancellation`](../15-async-and-cancellation)
- **A crash mid-run.** A wait between retries lives in memory; if the process
  dies during it, the wait is lost with the rest of the run. Recovering a run
  whose process died is
  [`20-durable-execution`](../20-durable-execution)
- **Retries of a whole group or child graph**: a parallel group's policy
  re-runs every branch ([`10-parallel-fan-out`](../10-parallel-fan-out)), a
  subgraph's re-runs the whole child
  ([`13-subgraph-composition`](../13-subgraph-composition))
- **Only `NodeException` is recovery-managed.** Any other exception type
  propagates out of `RunAsync` as it does from a plain node
- **`LastFailure` is not persisted or in a run event.** If the fallback itself
  is interrupted, a resume re-enters it without `LastFailure`
- **No live service.** The payment gateway is a deterministic stand-in, so the
  demo runs offline

## Run it

```bash
dotnet run --project demos/08-error-recovery/08-error-recovery.csproj
```

The demo deletes `recovery-checkpoint.db` (next to the built assembly, under
`bin/`, which is gitignored) at the start of each run, so a run never trips
over a stale checkpoint. It doesn't delete it at the end: SQLite's connection
pool keeps the file open for the life of the process. The demo checks every
claim it prints and exits non-zero if one fails.

Output of a real run:

```
=== Scenario 1: transient blip -- Retry(3), backoff 200 ms doubling to 2 s, fallback 'QueueForManualReview' ===
    [gateway] call 1 for ord-1001 ($129.99, tok_visa_4242): transient error: gateway timed out
    [gateway] call 2 for ord-1001 ($129.99, tok_visa_4242): transient error: gateway timed out
    [gateway] call 3 for ord-1001 ($129.99, tok_visa_4242): approved
  Completed after 3 gateway call(s). Waits before each retry: 200 ms, 400 ms
  State: OrderState { OrderId = ord-1001, Amount = 129.99, CardToken = tok_visa_4242, PaymentStatus = charged, ReceiptId = RCPT-ord-1001-3, Confirmed = True, ReviewNote =  }
  Only the successful attempt's reservation was committed: RCPT-ord-1001-3
  Checkpoint for 'order-1001': none (a recovered failure leaves nothing behind)

=== Scenario 2: rate limited (Retry-After: 30s) -- Retry(2), same backoff, fallback 'QueueForManualReview' ===
    [gateway] call 1 for ord-1002 ($54.00, tok_visa_4242): transient error: gateway unavailable (503)
    [gateway] call 2 for ord-1002 ($54.00, tok_visa_4242): transient error: gateway unavailable (503)
    [gateway] call 3 for ord-1002 ($54.00, tok_visa_4242): transient error: gateway unavailable (503)
  Retries exhausted after 3 gateway call(s). Waits: 2000 ms, 2000 ms
  The 30 s hint replaced the computed delay, capped at the policy's 2 s max.
  State: OrderState { OrderId = ord-1002, Amount = 54.00, CardToken = tok_visa_4242, PaymentStatus = manual_review, ReceiptId = , Confirmed = False, ReviewNote = ChargePayment failed (Transient) after 3 attempt(s): gateway unavailable (503); gateway asked to wait 30s }
  ReceiptId is null and 'SendConfirmation' never ran (Confirmed: False): the fallback ran on the state from before ChargePayment.

=== Scenario 3: card declined -- Retry(3), same backoff, no fallback ===
    [gateway] call 1 for ord-1003 ($899.00, tok_expired_0119): fatal error: card declined: expired
  Failed: node 'ChargePayment' gave up after 1 attempt(s) -- 'card declined: expired'. Retries scheduled: 0.
  A fatal error is never retried, so it never waits either.

=== Step 4: reading the Failed checkpoint back (store instance #2) ===
  status:         Failed
  failed node:    ChargePayment
  resume from:    ChargePayment
  frozen state:   {"OrderId":"ord-1003","Amount":899.00,"CardToken":"tok_expired_0119","PaymentStatus":"validated","ReceiptId":null,"Confirmed":false,"ReviewNote":null}
  metadata.attempts: 1
  metadata.error: card declined: expired
  metadata.error_kind: fatal
  The frozen state is the last committed state (after ValidateOrder). ReceiptId is null: the declined attempt's reservation never reached it.

=== Step 5: the customer updates their card; the Failed checkpoint is resumed (store instance #2) ===
    [gateway] call 1 for ord-1003 ($899.00, tok_visa_4242): approved
  State: OrderState { OrderId = ord-1003, Amount = 899.00, CardToken = tok_visa_4242, PaymentStatus = charged, ReceiptId = RCPT-ord-1003-1, Confirmed = True, ReviewNote =  }
  ChargePayment ran again (1 gateway call), ValidateOrder did not (0 runs), and SendConfirmation completed the order.
  Resuming the same checkpoint again is refused: checkpoint for thread 'order-1003' was already resumed

=== Step 6: a recovery policy is checked at .Compile(), not at runtime ===
  Rejected: recovery policy for 'ChargePayment' falls back to 'QueueForManualReveiw', which does not exist

=== Done: retries backed off, a rate limit's hint set the wait, the fallback knew why, and a fatal failure was resumed once its cause was fixed. ===
```

A record's `ToString()` prints a null field as blank (`ReceiptId = `). Step 5
uses a new gateway (its call counter starts at 1) and a new graph, as a
separate process would.

## Files

- `Program.cs` — the whole demo, single file (top-level statements plus the
  gateway simulation and `OrderState`)
- `08-error-recovery.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet, plus
  `Microsoft.Extensions.TimeProvider.Testing` for `FakeTimeProvider`
