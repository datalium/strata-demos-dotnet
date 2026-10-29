# 05 — hitl-interruption

Human-in-the-loop interruption (STRATA-SPEC-002) with typed human signals
([`design/21-Typed-Human-Signals.md`](https://github.com/datalium/strata/blob/main/design/21-Typed-Human-Signals.md) in `datalium/strata`). A
`PrepareWireTransfer` node drafts an outbound wire transfer, the graph
freezes at an interrupt boundary before anything irreversible happens, and
the human's answer decides what runs next: `ExecuteWireTransfer` on an
approval, `CancelWireTransfer` on a rejection. The checkpoints go to a real
SQLite file. The store instance that froze the runs is disposed, and a new,
independent one at the same file answers them, standing in for the process
after a restart.

## What it demonstrates

- **Freeze/Flush/Evict.** `RunAsync(...)` runs `PrepareWireTransfer`, saves
  the frozen state through `EfCoreCheckpointStore` (`Datalium.Strata.Storage`'s SQLite /
  EF Core adapter, opened on a file, not `CreateInMemory()`) and returns
  `GraphExecutionResult<TState>.Interrupted`. Nothing keeps running while the
  transfer waits
- **The checkpoint survives a restart.** Store instance #1 is disposed before
  store instance #2 is built. The only thing they share is the SQLite file,
  so every later step proves the checkpoints reached disk
- **Finding what is waiting after the restart.** Store #2 lists threads with
  `ListThreadsAsync(new ThreadQuery { States = { Interrupted } })`, the query
  port every store implements (`design/19`). Both transfers show up, waiting
  at `PrepareWireTransfer`
- **Typed overrides.** `HumanSignal.Approve("jdoe@acme.io").WithOverride("Amount", 4150.25m)`
  writes a JSON number into the frozen state, so it fits the `decimal`
  field. The 0.3-era `FieldOverrides` map writes every value as a JSON
  string. Step 3 shows what that does to a number: the resume is refused
  before the checkpoint is stamped, nothing runs, and the thread can still
  be answered
- **The recorded decision.** The resume that answers the boundary records the
  signal as a `HumanDecision` (boundary, approved or not, reviewer, every
  override typed, `DecidedAt`). `ExecuteWireTransfer` reads it from
  `NodeContext.Decision` and takes `ApprovedBy` from `Decision.Reviewer`, so
  the reviewer doesn't have to type their own name into the state
- **The reject route.** `.AddInterruptBoundary("PrepareWireTransfer", onReject: "CancelWireTransfer")`.
  `HumanSignal.Reject("mlee@acme.io")` continues at `CancelWireTransfer`
  instead of the next node fixed at freeze time, so `ExecuteWireTransfer`
  never sees a rejected transfer. The rejection is recorded as a decision
  too, and its override (a `Note`) still applies
- **Each answer is used once.** After both resumes, nothing is listed as
  waiting

## What it does *not* cover

- **No separate OS processes.** The restart is simulated by disposing one
  store and opening another at the same file. That is the part that matters
  (no shared in-memory state). For a real two-process hand-off, between .NET
  and Rust, see [`09-cross-language-interop`](../09-cross-language-interop)
- **A boundary with a timeout** (`AddInterruptBoundary(name, timeout:, onTimeout:)`):
  see [`19-waits-and-timers`](../19-waits-and-timers)
- **A boundary inside a subgraph**, and the decision staying with the level
  that answered it: see
  [`13-subgraph-composition`](../13-subgraph-composition), scenario 6
- **The decision on a later checkpoint.** Every checkpoint a level writes
  after an approval carries the decision, so a transfer that fails after
  approval and is resumed later still knows who approved it. Here nothing
  fails after the boundary, so no such checkpoint is written. Resuming a
  `Failed` checkpoint is in [`08-error-recovery`](../08-error-recovery)
- **Who may approve.** Strata records the reviewer's identity and doesn't
  judge it; authorization is the caller's job
- **No staleness check on resume** (for example, re-checking hours later that
  the transfer is still valid). Put that in the node after the boundary
- **No real bank.** `Bank` is an in-memory list, so the demo can count what
  was sent and runs offline

## Run it

```bash
dotnet run --project demos/05-hitl-interruption/05-hitl-interruption.csproj
```

The checkpoint file is `checkpoint.db` next to the built executable
(`bin/<config>/net10.0/`, covered by the `bin/` gitignore rule). It is
deleted at the start of each run. The demo checks every claim it prints and
exits non-zero if one fails.

Output of a real run (the SQLite path depends on your checkout and
configuration):

```
=== Step 1: two transfers run to the interrupt boundary (store instance #1) ===
  wire-acme-corp: suspended at PrepareWireTransfer, next ExecuteWireTransfer
    frozen: WireTransferState { Beneficiary = Acme Corp, Amount = 4200.50, Currency = USD, InvoiceGenerated = True, Status = awaiting approval, ApprovedBy = , Note =  }
  wire-globex: suspended at PrepareWireTransfer, next ExecuteWireTransfer
    frozen: WireTransferState { Beneficiary = Globex Ltd, Amount = 9800.00, Currency = USD, InvoiceGenerated = True, Status = awaiting approval, ApprovedBy = , Note =  }
  Store instance #1 disposed. The checkpoints now exist only in the SQLite file.

=== Step 2: after the restart, store instance #2 lists the transfers waiting for a human ===
  SQLite file: E:\GitHub\Datalium\strata-demos-dotnet\demos\05-hitl-interruption\bin\Release\net10.0\checkpoint.db
  wire-acme-corp: Interrupted, waiting at PrepareWireTransfer
  wire-globex: Interrupted, waiting at PrepareWireTransfer

=== Step 3: a string override for a decimal field is refused before the checkpoint is touched ===
  Refused: failed to rehydrate state for thread 'wire-acme-corp': The JSON value could not be converted to WireTransferState. Path: $.Amount | LineNumber: 0 | BytePositionInLine: 45.
  Not stamped, nothing ran: 'wire-acme-corp' can still be answered.

=== Step 4: jdoe approves wire-acme-corp, correcting the amount to the invoice total ===
  completed: WireTransferState { Beneficiary = Acme Corp, Amount = 4150.25, Currency = USD, InvoiceGenerated = True, Status = sent (ref 0001), ApprovedBy = jdoe@acme.io, Note =  }
  ExecuteWireTransfer saw: approved by jdoe@acme.io at PrepareWireTransfer, overrides Amount=4150.25
  ApprovedBy came from ctx.Decision.Reviewer. The signal set only Amount.

=== Step 5: mlee rejects wire-globex, so the run takes the reject route ===
  completed: WireTransferState { Beneficiary = Globex Ltd, Amount = 9800.00, Currency = USD, InvoiceGenerated = True, Status = cancelled by mlee@acme.io, ApprovedBy = , Note = beneficiary not on the vendor list }
  CancelWireTransfer saw: rejected by mlee@acme.io at PrepareWireTransfer, overrides Note="beneficiary not on the vendor list"
  Transfers sent in total: 1. ExecuteWireTransfer never ran for Globex.

=== Step 6: the waiting list is empty ===
  Threads waiting for a human: 0
```

A record's `ToString()` prints a null field as blank (`ApprovedBy = `). The
decision's `DecidedAt` timestamp is checked but not printed, since it changes
every run.

## Files

- `Program.cs` — the whole demo: top-level statements, the `WireTransferState`
  record and the in-memory `Bank`
- `05-hitl-interruption.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet
