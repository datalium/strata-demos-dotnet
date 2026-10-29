# 22 — graph-identity

Graph identity in checkpoints (`design/31-Graph-Identity.md`): shipping a new
version of a graph while threads the old version wrote are still suspended. A
shop's returns process, `Inspect -> Approve (a manager's sign-off) -> Refund`,
waits at `Approve` for days, so when version 2 ships some version 1 threads
are still in the store. Version 2 renames a state field (`Amount` ->
`RefundAmount`), adds two (`RefundMethod`, `Restocked`), renames a node
(`Refund` -> `IssueRefund`) and adds one after it (`Restock`).

Without an identity, nothing stops version 2 from reading a version 1 thread:
its frozen state has no `RefundAmount`, so as a `ReturnV2` the amount would be
0, and its next node, `Refund`, no longer exists. With one, version 2 refuses
the thread before it writes anything, unless a migration adopts it.

The Rust twin is
[`strata-demos-rust/demos/22-graph-identity`](https://github.com/datalium/strata-demos-rust/tree/main/demos/22-graph-identity);
its README says where the two engines differ.

## What it demonstrates

- **`CompilationOptions.GraphId` / `GraphVersion`.** Every checkpoint the
  graph writes carries `graph` = `{id, version}`, at `schema_version` 1.6.0.
  The demo reads the SQLite file directly: the head table has `graph_id` and
  `graph_version` columns
- **A version 1 thread refused by version 2, with nothing written** (3 rows
  before and after): `was written by graph 'returns' version 1, not version 2,
  and this graph has no migration`. The thread stays resumable by version 1
- **A different graph refused**, even with the same builder, node names and
  state type (`'exchanges'`), and so is a graph with no identity. `EndAsync`
  checks identity the same way
- **`CompiledGraph.Owns(checkpoint)`**, the cheap check a worker makes before
  resuming: version 2 owns its own threads, not version 1's, until it has a
  migration
- **`ThreadQuery.GraphId` / `GraphVersion`**: listing one version's waiting
  threads, to drain them with the old deployment (picked by
  `Checkpoint.Graph.Version` on the latest checkpoint) and to know when the
  old version can be retired
- **`CompilationOptions.Migrate`** adopting a version 1 thread into version 2:
  it sees the level as stored, refuses anything but version 1 (`null`),
  reshapes the frozen state JSON and renames the next node. It runs once,
  before anything is written, and not for version 2's own threads. The
  resume stamp is the migrated checkpoint: the file's row #1 for `R-2` has
  `graph_version` 2, `next_node` `IssueRefund` and the reshaped state

## What it does *not* cover

- **Identity inside a subgraph.** Each level carries its own graph's
  identity and is checked, and migrated, by its own graph
- **`WakeDueAsync` listing only its own graph's threads** (and
  `WakeOptions.MatchGraph`). For waits and the wake driver, see
  [`19-waits-and-timers`](../19-waits-and-timers)
- **The exported definition's `identity`**, `RunStarted.Graph`, the run
  span's `strata.graph.id` / `strata.graph.version` tags, and the CLI's
  `thread list --graph`
- **Automatic migration.** A migration is the application's code; the engine
  has no version order or schema diff

## Run it

```bash
dotnet run --project demos/22-graph-identity/22-graph-identity.csproj
```

Expected output:

```
=== Step 1: version 1 is deployed; two returns wait for approval ===
  R-1 ($120) waits at Approve, written by 'returns' version 1
  R-2 ($75) waits at Approve, written by 'returns' version 1

=== Step 2: version 2 is deployed, with no migration ===
  R-3 ($40) is new: version 2 runs it, and it waits at Approve, written by 'returns' version 2
    checkpoint_threads (the head table the query port reads):
      R-1  interrupted  graph_id returns  graph_version 1  schema_version 1.6.0
      R-2  interrupted  graph_id returns  graph_version 1  schema_version 1.6.0
      R-3  interrupted  graph_id returns  graph_version 2  schema_version 1.6.0
  Version 2 resumes R-1: thread 'R-1' was written by graph 'returns' version 1, not version 2, and this graph has no migration
  Checkpoint rows before and after: 3 and 3. R-1 still waits, resumable by version 1.
  v2.Owns(R-1): False, v2.Owns(R-3): True

=== Step 3: a different graph can't take a returns thread ===
  'exchanges' resumes R-1:   thread 'R-1' was written by graph 'returns' version 1, not 'exchanges' version 1
  An unnamed graph resumes R-1: thread 'R-1' was written by graph 'returns' version 1, and this graph has no identity
  'exchanges' ends R-1:      thread 'R-1' was written by graph 'returns' version 1, not 'exchanges' version 1

=== Step 4: the query port lists each version's waiting threads ===
  returns version 1, interrupted: R-1, R-2
  returns version 2, interrupted: R-3
  R-1 was written by version 1, so it goes to version 1:
    completed: inspected / sent for approval / refunded $120

=== Step 5: version 2 is redeployed with a migration for version 1 threads ===
  v2.Owns(R-2) without the migration: False; with it: True
  Version 2 resumes R-2: completed: inspected / sent for approval / refunded $75 to original-payment / restocked
  R-2's rows in the file: the resume stamp carries the migrated identity, state and next node:
    #0 graph_version 1  next_node Refund       frozen_state {"ReturnId":"R-2","Amount":75,"Inspected":true,"Refunded":false,"Log":["inspected","sent for approval"]}
    #1 graph_version 2  next_node IssueRefund  frozen_state {"ReturnId":"R-2","Inspected":true,"Refunded":false,"Log":["inspected","sent for approval"],"RefundAmount":75,"RefundMethod":"original-payment","Restocked":false}
  returns version 1, interrupted: none: version 1 can be retired

=== Done: what the file holds now ===
    checkpoint_threads (the head table the query port reads):
      R-1  resumed      graph_id returns  graph_version 1  schema_version 1.6.0
      R-2  resumed      graph_id returns  graph_version 2  schema_version 1.6.0
      R-3  resumed      graph_id returns  graph_version 2  schema_version 1.6.0
```

The demo checks each claim (the refusal messages, that refusals write
nothing, `Owns`, the listings by version, that the migration ran once and the
stamp is the migrated checkpoint) and exits non-zero if one fails. A completed
run writes nothing (the default `CheckpointPolicy.Boundaries`), so a finished
return's head is its resume stamp, `resumed`. The database is
`bin/<config>/net10.0/returns.db`, deleted at the start of each run.

## Files

- `Program.cs` — the whole demo, top-level statements in a single file
- `22-graph-identity.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `EfCoreCheckpointStore`) from NuGet
