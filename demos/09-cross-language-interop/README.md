# 09 — cross-language-interop

Freeze a graph in one runtime, resume it in the other. The wire-transfer graph
from [`05-hitl-interruption`](../05-hitl-interruption), split across two
**processes** and two **runtimes** through a single SQLite file: the .NET
runtime freezes at the interrupt boundary and exits; the Rust runtime opens
the file in a fresh process and resumes — or the other way around.

Strata's `CLAUDE.md` (in [`datalium/strata`](https://github.com/datalium/strata),
"One wire format") states a cross-language constraint: the Rust `strata` CLI
and runtime must be able to read checkpoints the C# runtime produces, and vice
versa. Until this demo that was only proven at *fixture* level — both
runtimes' unit tests agreed on a JSON shape — and no checkpoint had ever
crossed a real file between them. (It couldn't have: the two SQLite stores
wrote incompatible tables. See "What it took" below.)

This is the .NET half. The Rust twin is
[`strata-demos-rust/demos/09-cross-language-interop`](https://github.com/datalium/strata-demos-rust/tree/main/demos/09-cross-language-interop);
the two define the same graph, thread id, and state shape **independently**,
and the only thing they share is the database file.

> **Status with Strata 0.9.** Both halves were run against 0.9 in
> a pre-release build of `datalium/strata` (2026-10-24), in all four
> directions: .NET or Rust freezing, .NET or Rust resuming. A second resume is
> refused from either side, and the `strata` CLI reads the file whichever
> runtime wrote it. On 2026-10-29 the .NET half was run again against the
> published `0.9.0-preview.1` packages (.NET freezing, .NET resuming, the
> second resume refused), and the release `strata` binary read the file.

## What it demonstrates

- A checkpoint written by `EfCoreCheckpointStore` (.NET) is read and resumed by
  `SqliteCheckpointStore` (Rust), and one written by Rust is resumed by .NET —
  through a real file, across process boundaries, no shared code
- Each node stamps the state with the runtime that ran it, so the completed
  state shows the hand-off: `frozen_by = "dotnet", executed_by = "rust"` (or
  the reverse)
- The checkpoint history is **shared**, too: the resume marker one runtime
  writes is what the other runtime sees, so resuming a thread a second time —
  from either runtime — is refused ("already resumed")
- The `strata` CLI reads the same file (see below): `thread list`,
  `checkpoint log`, `checkpoint replay` work on a database either runtime
  produced
- Two things the two runtimes must agree on **by hand**, since the state is
  user-defined JSON inside the checkpoint:
  - **Field names.** The state is `snake_case` on the wire, so the record pins
    its names with `[JsonPropertyName]`. Without it, `System.Text.Json` writes
    `InvoiceGenerated` and `serde` can't find its fields
  - **Override keys.** A `HumanSignal` field override is merged into the frozen
    state's *JSON*, so it's keyed by the JSON name (`approved_by`) — not by the
    C# property name (`ApprovedBy`) that demo 05 uses when it stays within one
    runtime

## What it took (engine work in Strata)

Both SQLite stores were rewritten onto one shared layout. Before, they wrote
three mutually incompatible things:

| Writer | Table | Shape |
|---|---|---|
| Rust `SqliteCheckpointStore` | `checkpoints(thread_id, checkpoint_json)` | one row per thread, snake_case JSON |
| .NET `EfCoreCheckpointStore` | `Checkpoints(ThreadId, CheckpointJson)` | one row per thread, **PascalCase JSON, numeric `Status`** — not readable by `serde` |
| `strata` CLI | `checkpoints(thread_id, sequence, status, …)` | append-only log, one column per field |

SQLite table names are case-insensitive, so the Rust store and the CLI even
collided on the *same* table with different columns. All three now use the
CLI's append-only log (plus a `schema_version` column): `Save` appends the next
`sequence`, `Load` returns the latest. Each language's tests compare the created
table and a raw row against shared fixtures in [`schema/fixtures/checkpoint-log/`](https://github.com/datalium/strata/tree/main/schema/fixtures/checkpoint-log) in `datalium/strata`.

`EfCoreCheckpointStore` also no longer uses `EnsureCreated()`, which skips table
creation whenever the database already has *any* table and would emit its own
DDL. It runs the shared `CREATE TABLE IF NOT EXISTS` instead, a no-op against a
file the Rust runtime already initialised.

Since then the shared layout has grown, in both runtimes at once: columns for
nested, decision, wait and identity data, a `checkpoint_threads` head table kept
by a trigger (what `thread list` reads), `checkpoint_leases`, `checkpoint_blobs`,
and a `strata_schema_version` table (`checkpoints` is at version 2). A store
brings an older file up to date when it opens it, and refuses one a newer build
wrote (`design/34-Storage-Hardening.md`). The checkpoint this demo writes is
still a `1.0.0` row: an interrupt, no nesting, no wait, no identity.

## What it does *not* cover

- **Not automated.** It's a manual, two-command demo, not a test — a live
  cross-runtime test would need both toolchains in one CI job. Strata
  pins the layout with shared-fixture tests in each language instead
- **No lease.** One process at a time; two racing on one thread would collide
  on the primary key rather than corrupt the log. Thread leases
  (`RunOptions.Lease`, `design/32-Thread-Leases.md`) fence a thread between
  processes and work on this SQLite layout; see
  [`21-thread-leases`](../21-thread-leases). The demo doesn't use one, to keep
  what crosses between the runtimes to the checkpoint alone
- **No graph definition sharing.** Each side builds the graph in code; only the
  *checkpoint* crosses. Nothing loads a runnable graph from a definition file.
  Graph identity (`CompilationOptions.GraphId` / `GraphVersion`,
  `design/31-Graph-Identity.md`) can stamp checkpoints so a resume by a
  different graph is refused; see [`22-graph-identity`](../22-graph-identity).
  Not used here, for the same reason
- **No checkpoint per node.** The graph checkpoints only at its boundary, so
  after the resume the thread's latest row is the `interrupted` one with
  `resumed_at` set: the CLI shows it as `resumed`, not `completed`. A graph
  compiled with `CheckpointPolicy.EveryNode` writes `running` and `completed`
  rows (checkpoint `1.7.0`); see
  [`20-durable-execution`](../20-durable-execution)
- **Postgres and Redis.** The same hand-off through a server is demo 12
- **No live service or LLM** — same wire-transfer nodes as demo 05

## Run it

Both runtimes default to the same file
(`<temp dir>/strata-demo-09-interop/checkpoints.sqlite`), so no argument is
needed. `freeze` deletes and recreates it; both modes take an optional path as
a second argument.

**.NET freezes, .NET resumes** (verified with Strata 0.9)

```bash
# from strata-demos-dotnet
dotnet run --project demos/09-cross-language-interop/09-cross-language-interop.csproj -- freeze
dotnet run --project demos/09-cross-language-interop/09-cross-language-interop.csproj -- resume
```

**.NET freezes, Rust resumes** (verified with 0.9)

```bash
# from strata-demos-dotnet
dotnet run --project demos/09-cross-language-interop/09-cross-language-interop.csproj -- freeze
# from strata-demos-rust
cargo run -p cross-language-interop -- resume
```

**Rust freezes, .NET resumes** (verified with 0.9)

```bash
# from strata-demos-rust
cargo run -p cross-language-interop -- freeze
# from strata-demos-dotnet
dotnet run --project demos/09-cross-language-interop/09-cross-language-interop.csproj -- resume
```

**Inspect the file with the `strata` CLI** (the binary from the
[`v0.9.0-preview.1` release](https://github.com/datalium/strata/releases/tag/v0.9.0-preview.1); verified with 0.9)

```bash
strata thread list --storage <path-printed-by-freeze>
strata thread inspect wire-transfer-xlang --storage <path>
strata checkpoint log wire-transfer-xlang --storage <path>
strata checkpoint replay wire-transfer-xlang --sequence 0 --storage <path>
```

The CLI reads `postgres://` and `redis://` URLs through `--storage` too (demo 12).

Expected output — `freeze`, then `resume`, then `resume` again, all from .NET
(the path is the temp dir on Windows):

```
=== [dotnet] freeze: run to the interrupt boundary ===
  Checkpoint log: %TEMP%\strata-demo-09-interop\checkpoints.sqlite
  Interrupted at 'PrepareWireTransfer' (thread 'wire-transfer-xlang'); next node on resume: 'ExecuteWireTransfer'
  Frozen state: WireTransferState { InvoiceGenerated = True, Amount = 4200.5, Currency = USD, ApprovedBy = , IsCompleted = False, FrozenBy = dotnet, ExecutedBy =  }

  This process is about to exit; the checkpoint now exists only in the file.
  Next: run the `resume` step -- from the other runtime to see the hand-off.

=== [dotnet] resume: pick up a checkpoint another process froze ===
  Checkpoint log: %TEMP%\strata-demo-09-interop\checkpoints.sqlite
  Resumed and ran to completion.
  Completed state: WireTransferState { InvoiceGenerated = True, Amount = 4200.5, Currency = USD, ApprovedBy = compliance_officer_07, IsCompleted = True, FrozenBy = dotnet, ExecutedBy = dotnet }

  frozen_by = "dotnet", executed_by = "dotnet"

=== [dotnet] resume: pick up a checkpoint another process froze ===
  Checkpoint log: %TEMP%\strata-demo-09-interop\checkpoints.sqlite
error: resume failed: checkpoint for thread 'wire-transfer-xlang' was already resumed (was this thread already resumed? run `freeze` again)
```

The second `resume` exits 1. (`ApprovedBy = ` is blank in a record's
`ToString()` when it's null.) After a Rust freeze the `resume` output is the
same with `FrozenBy = rust` and `frozen_by = "rust", executed_by = "dotnet"`,
and the second resume is refused whichever runtime you use: the resume marker
crosses the file too.

The CLI on that file, after the resume (timestamps will differ):

```
$ strata thread list --storage <path>
+---------------------+---------------------+-------------+---------+-------------+----------------------+
| Thread ID           | Current Node        | Sequence ID | State   | Waiting For | Updated At           |
+---------------------+---------------------+-------------+---------+-------------+----------------------+
| wire-transfer-xlang | PrepareWireTransfer | 1           | resumed | -           | 2026-10-24T13:34:30Z |
+---------------------+---------------------+-------------+---------+-------------+----------------------+

$ strata checkpoint log wire-transfer-xlang --storage <path>
#0    [interrupted] node=PrepareWireTransfer -> ExecuteWireTransfer at=2026-10-24T13:34:29Z
#1    [interrupted] node=PrepareWireTransfer -> ExecuteWireTransfer at=2026-10-24T13:34:29Z

$ strata checkpoint replay wire-transfer-xlang --sequence 0 --storage <path>
Loading historical state from Checkpoint #0... Done.
Suspended at node: PrepareWireTransfer
Would resume at node: ExecuteWireTransfer
State at this checkpoint:
{
  "amount": 4200.5,
  "approved_by": null,
  "currency": "USD",
  "executed_by": null,
  "frozen_by": "dotnet",
  "invoice_generated": true,
  "is_completed": false
}
```

Row `#1` is the resume marker: the same checkpoint again with `resumed_at` set.
Before the resume, `thread list` shows sequence `0` in state `interrupted`.

## Files

- `Program.cs` — the whole demo, single file (`freeze` / `resume` modes,
  top-level statements plus the `WireTransferState` record)
- `09-cross-language-interop.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet
