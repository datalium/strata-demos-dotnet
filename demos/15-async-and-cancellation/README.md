# 15 — async-and-cancellation

Async execution, node context and cancellation (`design/17-Async-Execution.md`):
a vendor-payment run (`LoadInvoice` -> `Screen` -> `ScoreFraud` ->
`PostToLedger` -> `PayVendor`) whose handlers are mostly async. Each async
handler takes `(state, NodeContext, CancellationToken)` and returns a
`ValueTask<TState>`. The demo shows what the context tells a handler, how a
per-node timeout becomes a retry, that the engine doesn't wait for a handler it
has given up on, and that a cancelled run is a resumable outcome, not an
exception.

The Rust twin is
[`strata-demos-rust/demos/15-async-and-cancellation`](https://github.com/datalium/strata-demos-rust/tree/main/demos/15-async-and-cancellation);
its README says where the two engines differ.

## What it demonstrates

- **Async handlers and `NodeContext`** (scenario 1). `AddNode`,
  `AddFallibleNode` and `ParallelBranches.Branch` each take an async handler.
  Its context carries the thread id, the node's own name and its qualified
  path (`Screen/Sanctions` for a branch), the attempt, the run id and the run
  metadata. A sync handler (`Func<TState, TState>`, here `PostToLedger`) gets
  no context and no token
- **Run-level inputs in `RunOptions`**: `Metadata` (`tenant`), `RunId`,
  `Services` (an `IFxRates` that `LoadInvoice` resolves from
  `ctx.Services`), `Deadline` and `TimeProvider`
- **`SetTimeout(node, TimeSpan)`** (scenario 2). The fraud API hangs on
  attempt 1. When its 2 s timeout elapses, the attempt's token is cancelled
  and the attempt fails as **transient**. `Retry(1)` runs attempt 2 with a
  fresh timeout, and it succeeds. Without the retry, the run ends `NodeFailed`
  with `node 'ScoreFraud' timed out after 2000 ms` and a `failed` checkpoint
  whose `error_kind` is `transient`
- **The engine never waits for a handler it gave up on** (scenario 3). The
  ledger SDK is synchronous and its first call blocks on a gate that the demo
  holds shut. The timeout fires, the retry posts, and the run **completes
  while call 1 is still blocked**. Then the demo opens the gate. The
  abandoned call's return value is discarded, but its side effect still
  happens: the invoice is posted twice. Sync work is abandoned, not stopped.
  A handler whose side effects must not overlap a retry should be async and
  honour its token
- **Cancellation by token** (scenario 4). An operator cancels during the bank
  call. `RunAsync` *returns* `Cancelled` (node `PayVendor`, reason
  `Requested`, `StoppedAt [PayVendor]`). It does not throw. The bank client
  sees its token cancelled. `PayVendor`'s `Retry(2)` never runs, because a
  cancellation is not a failure. The `cancelled` checkpoint (schema `1.2.0`)
  holds the state `PayVendor` started with, plus `cancel_reason = requested`
  next to the run's `tenant`
- **Resuming a cancelled thread**. `ResumeAsync` re-enters `PayVendor` only, at
  attempt 1. The invoice isn't loaded, screened or posted again. The resume
  passes no metadata, and the handler still sees `tenant = acme-eu`, because
  a resume carries the stored run metadata forward. A second resume of the
  same checkpoint is refused (`already resumed`)
- **Cancellation by deadline** (scenario 5). `RunOptions.Deadline` passes
  during the bank call: `Cancelled` with reason `Deadline` and
  `cancel_reason = deadline`. A resume with a fresh deadline completes the run
- **A fake clock**. Node timeouts and the deadline run on
  `RunOptions.TimeProvider`. The demo passes a `FakeTimeProvider`, waits until
  the slow handler has started, and then advances the clock. Every timeout
  fires exactly when the demo means it to, and no scenario waits for real
  time to pass

## What it does *not* cover

- **Retry backoff and `NodeException.Transient(message, retryAfter)`**
  (`design/25-Retry-Backoff.md`). The retries here are immediate
- **Cancellation inside a subgraph or a parallel group**. A cancelled child
  nests its `cancelled` checkpoint in the parent's, and a subgraph node's
  timeout fails it transiently with the child's checkpoint nested (see
  [`CancellationTests.cs`](https://github.com/datalium/strata/blob/main/dotnet/tests/Datalium.Strata.Tests/CancellationTests.cs) in `datalium/strata`). [`13-subgraph-composition`](../13-subgraph-composition)
  covers subgraphs. Per-branch timeouts are `design/26-Parallel-Group-Controls-And-Joins.md`
- **Streaming**. A cancelled stream ends with no `NodeEnd` for the stopped
  node. [`06-token-streaming`](../06-token-streaming) covers streaming
- **Run events and telemetry**: [`27-run-observability`](../27-run-observability). The run id
  and metadata appear here only as `NodeContext` sees them
- **`History` in `RunOptions`**. The context's history store is left unset
- **A real service**. The fraud API, ledger SDK and bank are deterministic
  stand-ins. Nothing goes over a network

## Run it

```bash
dotnet run --project demos/15-async-and-cancellation/15-async-and-cancellation.csproj
```

It takes about a second. Nothing in it waits on real time, so the output is
identical on every run:

```
=== Scenario 1: a healthy payment run -- async handlers and their NodeContext ===
  RunOptions: Metadata { tenant = acme-eu }, RunId = payrun-2026-10-24, Services = { IFxRates }
  What each async handler was told:
    node path            node         attempt  thread    run id             tenant
    LoadInvoice          LoadInvoice  1        inv-1001  payrun-2026-10-24  acme-eu
    Screen/Sanctions     Sanctions    1        inv-1001  payrun-2026-10-24  acme-eu
    Screen/Duplicates    Duplicates   1        inv-1001  payrun-2026-10-24  acme-eu
    ScoreFraud           ScoreFraud   1        inv-1001  payrun-2026-10-24  acme-eu
    PayVendor            PayVendor    1        inv-1001  payrun-2026-10-24  acme-eu
  PostToLedger is a sync handler (Func<Invoice, Invoice>): it gets no context and no token.
  Completed: Globex GmbH: USD 1250.00 = EUR 1150.00, fraud score 12, ledger LED-1001, payment PAY-7001

=== Scenario 2: the fraud API hangs -- SetTimeout(2 s) + Retry(1) ===
  Attempt 1 started, then the clock moved 2 s: its token was cancelled and the attempt failed as transient.
  ScoreFraud attempts seen in NodeContext.Attempt: [1, 2] -- each retry gets a fresh timeout.
  Completed: Initech: EUR 480.00, fraud score 12, ledger LED-1001, payment PAY-7001
  The same hang with no retry:
    NodeFailed: 'ScoreFraud' after 1 attempt(s): node 'ScoreFraud' timed out after 2000 ms
    Checkpoint: Failed, error_kind = transient

=== Scenario 3: the legacy ledger SDK blocks -- a sync handler is abandoned, SetTimeout(5 s) + Retry(1) ===
  The run completed while ledger call 1 was still blocked: True
  Ledger calls: 2; entries when the run ended: [LED-1002 inv-1003]
  Completed: Umbrella Ltd: EUR 99.90, fraud score 12, ledger LED-1002, payment PAY-7001
  Now the SDK's first call returns. Its result is discarded, but its side effect is not:
    ledger entries: [LED-1002 inv-1003, LED-1001 inv-1003] -- one invoice, posted twice.
  A handler whose side effects must not overlap a retry should be async and honour its token.

=== Scenario 4: an operator cancels during the bank call -- Cancelled, then resumed ===
  Cancelled at 'PayVendor', reason Requested, stopped at [PayVendor] (RunAsync returned it; it didn't throw).
  The bank client saw its token cancelled. Bank calls: 1 -- PayVendor's Retry(2) never ran: a cancellation isn't a failure.
  Checkpoint: Cancelled, schema 1.2.0, next node PayVendor, metadata { cancel_reason = requested, tenant = acme-eu }
    frozen state: LedgerRef = LED-1001, PaymentRef = null (the state PayVendor started with)
  Resumed: only [PayVendor attempt 1, tenant acme-eu] ran.
  Invoice loads: 1, ledger calls: 1, bank calls: 2.
  Completed: Hooli: USD 3200.00 = EUR 2944.00, fraud score 12, ledger LED-1001, payment PAY-7002
  A second resume of the same checkpoint: checkpoint for thread 'inv-1004' was already resumed

=== Scenario 5: the run's deadline passes during the bank call -- RunOptions.Deadline ===
  Cancelled at 'PayVendor', reason Deadline; checkpoint Cancelled, cancel_reason = deadline
  Resumed with a fresh deadline: Stark Industries: USD 7400.00 = EUR 6808.00, fraud score 12, ledger LED-1001, payment PAY-7002

=== Done: async handlers saw their context, timeouts became retries, a blocked handler was abandoned, and cancelled runs resumed where they stopped. ===
```

Every claim above is checked at runtime. If one stops holding, the demo
throws and exits non-zero.

## Files

- `Program.cs` — the whole demo: the five scenarios, the graph
  (`PaymentRun.Build`), and the stand-in services
- `15-async-and-cancellation.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `InMemoryCheckpointStore`) from NuGet, plus
  `Microsoft.Extensions.TimeProvider.Testing` for `FakeTimeProvider`
