# 25 — long-term-memory

Long-term memory (`design/45-Long-Term-Memory.md`): `StrataMemory` in
`Datalium.Strata.AI`, over the engine's `IMemoryStore` port. `RememberAsync`
and `RememberManyAsync` embed a text and store it with its vector.
`RecallAsync` embeds a text and returns the most similar records, by exact
cosine similarity. The store never calls a model. It takes vectors, and
`StrataMemory` makes them with an
`IEmbeddingGenerator<string, Embedding<float>>`.

The demo is an incident desk. Past incidents are remembered for two tenants.
A graph, `Recall -> Diagnose -> Remember`, recalls the incidents most like a
new one, reuses the closest one's fix, and remembers the new incident, so the
next run can recall it. The tenant comes from the run's metadata (`tenant`),
not from the nodes' code.

**The embedder is a stand-in.** `BagOfWords` is a small deterministic
`IEmbeddingGenerator`. Each word (lower-cased, stop words dropped, a crude
suffix strip) adds 1 to one of 256 buckets, chosen by an FNV-1a hash. FNV-1a
is used because `string.GetHashCode` changes from process to process. Texts
that share words really are closer, so the ranking is meaningful, though far
cruder than a real model's. The store is the engine's `InMemoryMemoryStore`
with a `FakeTimeProvider` as its clock. No network, no API key, and every run
prints the same thing.

The Postgres (pgvector), SQLite (sqlite-vec) and Redis 8 (Query Engine)
stores in `Datalium.Strata.Storage` (`PostgresMemoryStore`,
`SqliteMemoryStore`, `RedisMemoryStore`) implement the same `IMemoryStore` /
`IMemoryRetention` ports, and the code above the store would not change. This
demo doesn't use them, so it needs no server and no native extension.

The Rust twin is
[`strata-demos-rust/demos/25-long-term-memory`](https://github.com/datalium/strata-demos-rust/tree/main/demos/25-long-term-memory);
its README says where the two engines differ.

## What it demonstrates

- **Remembering** (step 1). `RememberManyAsync` checks every entry, embeds
  all six texts in **one** embedding call, and writes them in one
  `UpsertManyAsync`. Each record keeps its text (`Text`), the application's
  JSON (`Content`: root cause and action) and string metadata (`service`).
  The vectors are labelled `demo/bow-256`, which is `<provider>/<model>` from
  the generator's metadata
- **Recall inside a graph** (step 2). `Recall` asks for at most 3 matches
  with `MinSimilarity = 0.5`, excluding the incident's own id. `Diagnose`
  reuses the closest match's fix, or escalates when nothing is close enough.
  `Remember` stores the new incident. Each run makes two embedding calls,
  observed as `ModelCallStarted` events with `Operation = "embeddings"`. The
  second run recalls `inc-201`, which the first run remembered
- **Tenants** (step 2). Both tenants have an incident `inc-101` with the same
  symptoms and different causes. The key is (namespace, tenant, id), so both
  exist. A globex run recalls only globex's record and reuses globex's fix. A
  recall with no tenant reads only tenant-less records, here none: it never
  means "every tenant"
- **Filters** (step 3), applied before ranking: metadata equality
  (`service=search`), `MinSimilarity`, `Limit` (the top N of the same
  ranking) and `Exclude`. `inc-101` and `inc-103` share exactly the same
  query words, so they tie at 0.655, and a tie is ordered by id
- **Namespaces** (step 3). The runbook `rb-oom` is in namespace `runbooks`. A
  recall in `incidents` never returns it, however similar it is
- **Vectors are labelled by model** (step 4). A second `StrataMemory` whose
  generator names `bow-256-v2` makes the very same vectors, but its recall
  finds nothing. A search reads only its own label's records, so two models'
  vectors are never compared
- **An upsert keeps `CreatedAt`** (step 5). Remembering `inc-101` again 300
  days later replaces its content and moves `UpdatedAt`. `CreatedAt` stays at
  the first write. The timestamps are the store's, from its clock
- **Purging by age** (step 6). `PurgeAsync(new MemoryPurge(cutoff) {
  Namespace = "incidents" })` deletes the records last written before the
  cutoff, for every tenant. That removes 10 records. `inc-101` survives because
  its upsert at day 300 is recent, and the runbook survives because it is in
  another namespace

Each claim is checked when the demo runs, and the demo exits non-zero if one
fails.

## What it does *not* cover

- **The SQL and Redis stores**, their dimensions check on open, the HNSW
  index option and their metrics (see above). [`12-pluggable-storage`](../12-pluggable-storage)
  shows the same swap-the-store idea for checkpoints and history.
- **Hosting**: `AddPostgresMemory()` / `AddMemory()` /
  `AddMemoryRetention(...)`, which register the store, `StrataMemory` and a
  retention driver that calls `PurgeAsync` on a timer. The demo builds
  `StrataMemory` by hand and calls `PurgeAsync` itself.
- **Re-embedding after a model change**: list a namespace with `ListAsync`
  and remember each record's `Text` again with the new model. The key doesn't
  include the model, so each new record replaces its old one.
- **Embedding failures.** A failed embedding call is a model call's failure,
  a `NodeException` that is transient with the provider's hint or fatal,
  which a node's `RecoveryPolicy` can retry. Nothing is stored when it fails.
  The stand-in never fails.
- **Bad input**, refused with an `ArgumentException`. An empty id or a limit
  out of range is refused before anything is embedded. A vector of the wrong
  length, or one that is all zeros, is refused by the store.

## Run it

```bash
dotnet run --project demos/25-long-term-memory/25-long-term-memory.csproj
```

Output of a real run (identical on every run):

```
=== Step 1: remember past incidents (one embedding call for six entries) ===
  acme: 6 incidents remembered in 1 embedding call(s), labelled 'demo/bow-256'
  globex: 1 incident (same id inc-101, same symptoms, another cause); acme: 1 runbook in namespace 'runbooks'

=== Step 2: an incident desk that recalls similar incidents (Recall -> Diagnose -> Remember) ===
  [acme] inc-201: "Checkout pods crash looping again, out of memory after this morning's deploy"
      recalled inc-101 (0.802): a memory leak in the v2.3 image
      -> likely a memory leak in the v2.3 image (like inc-101); action: roll back the deploy
  [acme] inc-202: "Checkout out of memory, pods crash looping after the deploy"
      recalled inc-101 (0.926): a memory leak in the v2.3 image
      recalled inc-201 (0.866): likely a memory leak in the v2.3 image (like inc-101)
      -> likely a memory leak in the v2.3 image (like inc-101); action: roll back the deploy
  [globex] gx-7: "Checkout pods crash looping again, out of memory after this morning's deploy"
      recalled inc-101 (0.802): a noisy neighbour on the node
      -> likely a noisy neighbour on the node (like inc-101); action: move the pods to another node pool
  [acme] inc-203: "Billing emails delayed, the mail queue backlog keeps growing"
      -> no similar incident; action: page the on-call engineer
  The same recall with no tenant (no run metadata): 0 match(es)

=== Step 3: filters -- metadata, minimum similarity, limit, excluded ids, namespace ===
  everything (limit 10)      inc-101 0.655, inc-103 0.655, inc-202 0.471, inc-201 0.408, inc-102 0.000, inc-104 0.000, inc-105 0.000, inc-106 0.000, inc-203 0.000
  metadata service=search    inc-103 0.655
  MinSimilarity 0.5          inc-101 0.655, inc-103 0.655
  Limit 3                    inc-101 0.655, inc-103 0.655, inc-202 0.471
  Exclude [inc-101]          inc-103 0.655, inc-202 0.471, inc-201 0.408, inc-102 0.000, inc-104 0.000, inc-105 0.000, inc-106 0.000, inc-203 0.000
  namespace 'runbooks': rb-oom 0.696

=== Step 4: another model's memory reads none of these records ===
  'demo/bow-256-v2' recall: 0 match(es); 'demo/bow-256' recall: 9

=== Step 5: remembering inc-101 again 300 days later ===
  created 2026-01-05, updated 2026-11-01, root cause: a memory leak in the v2.3 image (postmortem PM-17)

=== Step 6: 400 days in, purge incidents not written for 365 days ===
  Purged 10 record(s) last written before 2026-02-09.
  Left: acme incidents [inc-101], globex incidents [], acme runbooks [rb-oom]

=== Done: memories were recalled by meaning, kept apart by tenant, namespace and model, updated in place and purged by age. ===
```

The similarities are the bag-of-words embedder's. A real model gives other
numbers, and usually much higher ones: StrataOps measured 0.98 to 1.00
between incidents on OpenAI's embeddings, which is why the design says to
rank rather than rely on a threshold. A `0.000` match shares no word with the
query. With no `MinSimilarity` it is still returned, last. The dates come
from the fake clock, which starts at 2026-01-05.

## Files

- `Program.cs` — the six steps, the incident desk graph, and `BagOfWords`
  (the stand-in embedder)
- `25-long-term-memory.csproj` — references `Datalium.Strata`,
  `Datalium.Strata.Storage` and `Datalium.Strata.AI`, plus
  `Microsoft.Extensions.TimeProvider.Testing` for the fake clock
