# 12 — pluggable-storage

One graph, one scenario, **four storage backends**: in-memory, SQLite, Postgres
and Redis. A wire-transfer graph (the one from
[`05-hitl-interruption`](../05-hitl-interruption)) freezes at its interrupt
boundary, every connection is dropped, and a brand-new one resumes it — while a
chat history is kept alongside in the same backend. The scenario is written
once and run against all four; nothing in it knows which backend it is talking
to.

`design/03-Architecture.md` promises that persistence is pluggable —
`CheckpointStore` and `HistoryStore` are traits, and "in-memory → SQLite →
Redis/PostgreSQL" are just implementations. Until this demo only SQLite
existed, so "pluggable" meant "two SQL-shaped things". This demo is the test of
the claim.

This is the .NET half. The Rust twin is
[`strata-demos-rust/demos/12-pluggable-storage`](https://github.com/datalium/strata-demos-rust/tree/main/demos/12-pluggable-storage).

> **Status with Strata 0.9.** Both halves were run against 0.9 in
> a pre-release build of `datalium/strata` (2026-10-24), against all four
> backends. The cross-runtime hand-off was run both ways, .NET to Rust and
> Rust to .NET, through Postgres and through Redis, and a second resume is
> refused from either side. On 2026-10-29 the .NET half was run again against
> the published `0.9.0-preview.1` packages: the matrix, and a .NET freeze and
> resume through Postgres and through Redis.

## What it demonstrates

- **The same code path against four backends.** `RunScenarioAsync` freezes a graph,
  drops the stores, resumes from fresh ones, reads the history back and checks a
  second resume is refused — and the only place that names a concrete store is
  `Backend.OpenAsync`
- **Redis is not SQL-shaped, and the interfaces didn't change.** There are no tables
  and no `MAX(sequence) + 1`: a thread's checkpoint log and a session's history
  are each one Redis list (a checkpoint is appended by a Lua script that also
  keeps the query index and read with `LINDEX`; a message is an `RPUSH`, read
  back with `LRANGE -n -1`). That the same interfaces admit it is the actual proof of
  "pluggable" — a Postgres adapter alone would only show SQL-to-SQL
- **Durability differs, visibly.** The in-memory store *loses* the checkpoint when
  it is "restarted" (the demo asserts that, as expected); the other three keep it
- **Concurrent writers (Postgres, Redis).** Four connections append to one session
  at once; nothing is lost and each writer's messages stay in order. Postgres does
  it with a per-session `pg_advisory_xact_lock` taken before choosing the next
  `sequence`; Redis needs nothing (`RPUSH` is atomic). SQLite's adapters assume a
  single writer, and the demo says so instead of trying
- **Opening a server store, the 0.9 way.** The Postgres stores' constructors don't
  touch the network; `PostgresCheckpointStore.OpenAsync(url)` /
  `PostgresHistoryStore.OpenAsync(url)` create or migrate the tables first, so an
  unreachable database (or one a newer build wrote) fails at startup. Redis uses
  `RedisCheckpointStore.ConnectAsync(url, new RedisStoreOptions { Prefix = ... })`.
  Every call either store makes retries a transient failure inside the store
  (`design/34-Storage-Hardening.md`). The stores are `IAsyncDisposable`
- **Delta checkpoints, written by one store and read by another** (`design/41-Checkpoint-Efficiency.md`).
  After the matrix, each durable backend runs a six-node graph compiled with
  `CheckpointPolicy.EveryNode` (seven checkpoints: `running` before the first node
  and after each of the next five, `completed` after the last) on a store opened
  with `Deltas = new DeltaOptions()`. Five of the seven are stored as deltas (only
  the keys that changed), counted from the stores' own
  `strata.store.checkpoint.size` telemetry. A fresh store opened **without**
  `Deltas` then reads the thread's whole log back through `GetLogAsync`: every row
  whole, in order, with the version the engine wrote. A reader needs no options to
  decode them, and the `strata` CLI reads them too
- **Cross-runtime, through a real server.** The Postgres tables and the Redis keys
  are shared with the Rust stores, so demo 09's hand-off works through them too
  (below): each runtime resumes the other's frozen thread and reads the other's
  history

## What it took (work in Strata)

Two new adapters per store per language, plus one bug the demo found:

| | Rust (`strata-memory`) | .NET (`Datalium.Strata.Storage`) |
|---|---|---|
| Postgres | `postgres_store::{PostgresCheckpointStore, PostgresHistoryStore}` (sync `postgres` crate) | `Postgres.{PostgresCheckpointStore, PostgresHistoryStore}` (plain Npgsql) |
| Redis | `redis_store::{RedisCheckpointStore, RedisHistoryStore}` (sync `redis` crate) | `Redis.{RedisCheckpointStore, RedisHistoryStore}` (StackExchange.Redis) |

- **No trait changes.** `CheckpointStore`/`HistoryStore` (and the C# interfaces)
  needed nothing to admit either backend
- **Layouts pinned across runtimes** by shared fixtures — `postgres-columns.json`
  for the two Postgres tables and the checkpoint wire JSON for Redis — asserted by
  both languages' tests (`schema/README.md`: "Postgres checkpoint log", "Redis
  layout")
- **A .NET SQLite bug, fixed** (found by *this* demo). `EfCoreHistoryStore` created its table with
  `EnsureCreated()`, which silently does nothing when the file already has *any*
  table. Opened after `EfCoreCheckpointStore` on the same file, its first query hit
  "no such table" — the Rust stores could share a file, the .NET ones couldn't. It
  now runs `CREATE TABLE IF NOT EXISTS`, like the checkpoint store already did.
- Contract tests now run the same assertions against all four backends, and the
  Postgres/Redis ones need live servers (`STRATA_TEST_POSTGRES_URL` /
  `STRATA_TEST_REDIS_URL`); both CI workflows start service containers for them

Since then (0.4-0.9) the stores have gained, in both runtimes: the query port
(`ListThreadsAsync`, `GetLogAsync`) with a head table or index, retries inside
every call, `OpenAsync` / `MigrateAsync` and a `strata_schema_version` table,
retention (`PurgeAsync`, and an opt-in Redis TTL for finished threads), thread
leases, group commit (Postgres, SQLite), delta checkpoints and value offload.
None of it changed the interfaces this demo's scenario uses.

## What it does *not* cover

- **Retention.** Every built-in store has `PurgeAsync(olderThan, states, limit)`
  (deletes finished threads by age), and Redis can expire finished threads
  (`RedisStoreOptions.FinishedThreadTtl`, off by default). The demo calls
  neither, so what it writes stays until you drop the containers
- **Group commit and value offload** (`GroupCommit`, `Offload` on the same
  options records). Only deltas are shown
- **Leases and recovery.** Thread leases (demo [`21-thread-leases`](../21-thread-leases))
  and crash recovery of a durable thread (demo [`20-durable-execution`](../20-durable-execution))
  work on these backends; the scenario here runs one writer per thread
- **TLS through a `redis://` URL.** A Postgres URL honours `sslmode`; for Redis
  pass a StackExchange.Redis configuration string or your own `IDatabase`. No
  Redis Cluster or Sentinel
- **SQLite history isn't cross-runtime**: the EF Core store writes a
  `"HistoryRecords"` table with the role as `"User"`, Rust a `history_records`
  table with `"user"`. The Postgres and Redis history stores are shared. Demo 09
  is the SQLite hand-off, for checkpoints only
- **Not a benchmark.** Nothing here measures latency or throughput; the byte
  counts are what the stores report, not a measurement of the backend
- **Needs Docker** (or your own Postgres and Redis) — this is the first demo that
  needs external services. No LLM, no API key

## Run it

```bash
docker compose up -d --wait        # Postgres 16 + Redis 7, throwaway (see docker-compose.yml)
dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj   # the matrix: all four backends
docker compose down
```

To pick backends: `dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj -- matrix redis postgres`.
Connection URLs come from `STRATA_DEMO_POSTGRES_URL`
(default `postgres://strata:strata@localhost:5432/strata`) and
`STRATA_DEMO_REDIS_URL` (default `redis://localhost:6379`). If a server isn't
reachable the demo says so and how to start it, rather than hanging. Each
connect times out after 5 s, and the Postgres store tries a failed connect four
times with backoff before giving up, so that can take 15 s or so.

[`datalium/strata`](https://github.com/datalium/strata) and `strata-demos-rust` ship a `docker-compose.yml`
on the same ports — run one copy at a time.

**Cross-runtime hand-off** (Postgres or Redis; same thread id and Redis prefix in
both runtimes, so no argument beyond the backend)

```bash
# .NET freezes, Rust resumes (verified with 0.9)
# from strata-demos-dotnet
dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj -- freeze postgres
# from strata-demos-rust
cargo run -p pluggable-storage -- resume postgres

# Rust freezes, .NET resumes (verified with 0.9)
# from strata-demos-rust
cargo run -p pluggable-storage -- freeze redis
# from strata-demos-dotnet
dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj -- resume redis

# .NET freezes, .NET resumes (verified with Strata 0.9)
dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj -- freeze postgres
dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj -- resume postgres
```

The `strata` CLI (the binary from the [`v0.9.0-preview.1` release](https://github.com/datalium/strata/releases/tag/v0.9.0-preview.1))
reads the same thread from either server:

```bash
strata thread list --storage postgres://strata:strata@localhost:5432/strata
strata checkpoint log wire-transfer-pluggable --storage "redis://localhost:6379?prefix=strata-demo-12"
```

Expected output — the matrix (the byte counts are the JSON each store wrote;
Redis keeps the whole checkpoint in one list element, the SQL stores count the
`frozen_state` column):

```
=== one scenario, 4 storage backends ===
  freeze a wire-transfer graph -> drop every connection -> resume from a fresh one -> read history back

  in-memory  ok
  sqlite     ok
  postgres   ok
  redis      ok

  backend    survives restart       history  concurrent writers
  in-memory  no (lost, as expected) 3        in-process only
  sqlite     yes                    3        single writer assumed
  postgres   yes                    3        40 appends, none lost
  redis      yes                    3        40 appends, none lost

  Same graph, same assertions, same code path for all of them.

=== delta checkpoints: written with Deltas on, read by a store without them ===
  a durable graph (6 nodes, CheckpointPolicy.EveryNode) runs once on a store with Deltas; a fresh store
  opened without Deltas reads the thread's log back

  backend    rows read  stored whole  as deltas  bytes (whole / delta, average)
  sqlite     7          2             5          2163 / 133
  postgres   7          2             5          2163 / 133
  redis      7          2             5          2374 / 346

  Every row came back whole, in order: the reader needed no options to decode the deltas.
```

Expected output — `cargo run … -- freeze redis`, then `dotnet run … -- resume redis`
(a run with the Rust twin on 0.9, 2026-10-24; a .NET freeze gives the same with
`[dotnet]` history lines and `frozen_by = "dotnet"`):

```
=== [dotnet] resume on redis: pick up a thread another process froze ===
  Resumed and ran to completion.
  Completed state: WireTransferState { InvoiceGenerated = True, Amount = 4200.5, Currency = USD, ApprovedBy = compliance_officer_07, IsCompleted = True, FrozenBy = rust, ExecutedBy = dotnet }

  frozen_by = "rust", executed_by = "dotnet"

  History (as read from the store):
    user       [rust] Wire $4,200.50 to Acme Corp
    assistant  [rust] Prepared the transfer; waiting for approval.
    assistant  [dotnet] Transfer executed.
```

Running `resume` again prints `error: resume failed: … was already resumed …` and
exits 1, whichever runtime you use — the resume marker crosses the database too.
`freeze` after that starts a fresh checkpoint on the same thread (the log is
append-only, so the old one stays as history).

## What's actually stored

After the matrix and a .NET `freeze` / `resume` on Postgres
(`docker compose exec postgres psql -U strata -d strata`):

```
select thread_id, sequence, status, resumed_at is not null as resumed, left(frozen_state::text, 44)
  from checkpoints where thread_id = 'wire-transfer-pluggable';

        thread_id        | sequence |   status    | resumed |                     left
-------------------------+----------+-------------+---------+----------------------------------------------
 wire-transfer-pluggable |        0 | interrupted | f       | {"amount": 4200.5, "currency": "USD", "froze
 wire-transfer-pluggable |        1 | interrupted | t       | {"amount": 4200.5, "currency": "USD", "froze
```

Row 1 is the resume marker. A cross-runtime hand-off adds rows to the same log
the same way: whichever runtime froze writes the first, whichever resumed the
second. `frozen_state` is `JSONB`, so key order isn't what was saved (`amount`,
`currency`, … not declaration order) — harmless, state is read by field name.

The deltas thread, as stored (the run id differs per run):

```
select sequence, schema_version, status, length(frozen_state::text) as bytes
  from checkpoints where thread_id = 'ledger-<run id>-postgres' order by sequence;

 sequence | schema_version |  status   | bytes
----------+----------------+-----------+-------
        0 | 1.7.0          | running   |  2117
        1 | 1.8.0          | running   |   104
        2 | 1.8.0          | running   |   115
        3 | 1.8.0          | running   |   126
        4 | 1.8.0          | running   |   137
        5 | 1.8.0          | running   |   148
        6 | 1.0.0          | completed |  2181
```

`1.8.0` is a delta row: only `entries` changed, so only `entries` is stored.
Every store, and the CLI, decodes it back to the `1.7.0` checkpoint the engine
wrote. `select * from strata_schema_version` shows `checkpoints` at 2 and
`history` at 1.

In Redis the shared thread is a single list, one JSON checkpoint per element
(`docker compose exec redis redis-cli LRANGE strata-demo-12:checkpoints:wire-transfer-pluggable 0 -1`),
and the history is another, `strata-demo-12:history:wire-transfer-pluggable`.

## Files

- `Program.cs` — the whole demo, single file (`matrix` / `freeze` / `resume` modes)
- `12-pluggable-storage.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet; no other packages
- `../../docker-compose.yml` — the throwaway Postgres + Redis
