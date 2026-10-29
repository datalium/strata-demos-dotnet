# 18 — map-fan-out

Dynamic fan-out with a map (`design/27-Map-Fan-Out.md`). An accounts-payable
check: an invoice arrives with however many lines it has, and `AuditLines`
runs the same compiled child graph (`Classify` -> `CheckPrice` -> `Score`)
once per line before `Decide` approves the invoice or holds it for review.

```
AuditLines (map: one LineAudit child per invoice line, keyed by line id) -> Decide
```

How it differs from its neighbours: demo [10](../10-parallel-fan-out)'s
parallel group has branches fixed when the graph is built, and demo
[13](../13-subgraph-composition)'s subgraph runs one child graph inside one
node. A map reads a list from the state when the node starts, so how many
children run is only known at run time. To the parent it is still one node
and one hop.

The Rust twin is
[`strata-demos-rust/demos/18-map-fan-out`](https://github.com/datalium/strata-demos-rust/tree/main/demos/18-map-fan-out);
its README says where the two engines differ.

## What it demonstrates

- **`.AddMap(name, items, key, child, input, reducer, options)`**: `items`
  reads the lines from the state, `key` names each one, `child` is a
  `CompiledGraph<LineAudit>`, `input` builds each child's start state, and the
  plain reducer gets the children's final states **in list order**
- **The fan-out** (scenario 1): 12 lines, `MapExpanded` with every key in list
  order before any item starts, items started in list order, each item's nodes
  at **`AuditLines/<line id>/<node>`**, and 36 child nodes run (12 x 3)
- **`MaxConcurrency` is 8 by default and never unbounded** (scenarios 1 and
  1b): the price service sees exactly 8 lookups in flight with the default
  (`MapOptions<T>.DefaultMaxConcurrency`), and exactly 3 with `MaxConcurrency
  = 3`. The property is an `int`, not an `int?`, and `0` is refused (step 5)
- **The required key, and what fails the map before anything starts**
  (scenario 2): a duplicate key, a key containing `/`, an empty key, or more
  items than `MaxItems` fail the map **fatally**, with no `MapExpanded`, no item
  started and no lookup made. The list comes from the state, so a retry would
  see the same list: `Retry(3)` on the map doesn't run
- **A failing item, and how its kind propagates** (scenario 3). A child failure
  fails the item with the child's kind, and the map with the group runner's
  rule:
  - an unknown SKU is a fatal `NodeException` in the child's `CheckPrice`;
    neither the child's `Retry(1)` nor the map's `Retry(2)` runs, and the
    message names the item and the child node: `item 'L03': failed at
    'CheckPrice' after 1 attempt(s): unknown SKU 'INK-X9'`
  - a busy price service is transient with a 2 s retry hint. The child's own
    `Retry(1)` runs out first, then the item and the map fail transiently,
    and the map's failed checkpoint carries `strata.retry_after_ms = 2000`
  - with `Retry(1)` on the map, the retry emits a second `MapExpanded` and
    re-runs **every** line, the one that had succeeded included: there is no
    per-item retry
- **`.AddMapWithOutcomes(...)` with `Partial = KeepSucceeded`** (scenario 4):
  the reducer sees every line's `BranchOutcome` (`Name` is the key) in list
  order, with `JoinMet = false`. The line that failed is flagged for review with
  its own error and kind, and no other line is cancelled because of it
- **`.Compile()` refuses a child with an interrupt boundary** (step 5): a map
  item can't freeze for a human, so `map 'AuditLines' runs a graph with an
  interrupt boundary at 'Score'; a map item can't freeze`. The check looks
  into subgraphs inside the child too

The concurrency peaks are exact, not a matter of timing: the price list holds
each lookup until the expected number are in flight together, then lets
everything through.

## What it does *not* cover

- **Joins on a map** (`Join = FirstSuccess`, `FirstMatching`, `Quorum`): a
  map takes the same joins as a group, and demo
  [17](../17-parallel-joins) shows them on a group
- **`ItemTimeout`, `CancelSiblingsOnFailure`, and items winding down.** An
  item the map gives up on stops at its current child node and is waited for,
  so its child's `NodeCompleted` comes before the item's `BranchCompleted`;
  [`MapTests.cs`](https://github.com/datalium/strata/blob/main/dotnet/tests/Datalium.Strata.Tests/MapTests.cs) in `datalium/strata` covers it
- **Streaming from items** (chunks named `<map>/<key>/<node>`), a map inside a
  subgraph, the cancelled-run checkpoint, and the hop limit per item
- **The exported definition** (`kind: "map"` with the child as a template,
  `schema_version` `1.3.0`)
- **No live service.** The price list is an in-memory dictionary

## Run it

```bash
dotnet run --project demos/18-map-fan-out/18-map-fan-out.csproj
```

Takes about a second. Output (identical on every run):

```
=== Scenario 1: a 12-line invoice -- one child graph per line, default options ===
  MapExpanded: [L01, L02, L03, L04, L05, L06, L07, L08, L09, L10, L11, L12]
  Items started in list order: L01, L02, L03, L04, L05, L06, L07, L08, L09, L10, L11, L12
  Peak price lookups in flight: 8 (MapOptions.DefaultMaxConcurrency = 8); the other 4 lines waited for a slot.
  One item's child nodes: AuditLines/L07/Classify -> AuditLines/L07/CheckPrice -> AuditLines/L07/Score
  Child nodes run: 36 (12 lines x 3 nodes)
  Reducer got the audits in list order:
    L01 PAP-A4   paper      score 100
    L02 PAP-A3   paper      score 100
    L03 INK-K    toner      score 100
    L04 INK-C    toner      score 100
    L05 PEN-BLU  stationery score 100
    L06 PEN-RED  stationery score 100
    L07 PAD-A5   paper      score 100
    L08 FLD-GRN  stationery score 100
    L09 INK-M    toner      score  52  billed 79.50, list 71.00 (+12%)
    L10 SVC-INST service    score 100
    L11 PAP-A4   paper      score 100
    L12 SVC-DLV  service    score 100
  Decision: hold: L09 billed 79.50, list 71.00 (+12%)

=== Scenario 1b: the same invoice with MaxConcurrency = 3 (the price API's own limit) ===
  Peak price lookups in flight: 3; items still started in list order: True.

=== Scenario 2: bad lists fail before any line is looked at -- even with Retry(3) on the map ===
  an exported row repeated   -> fatal, attempts 1, lookups 0: map 'AuditLines': items 1 and 2 have the same key 'L02'
  a split line               -> fatal, attempts 1, lookups 0: map 'AuditLines': item 1 has the key 'L02/1', which contains '/'
  a line with no id          -> fatal, attempts 1, lookups 0: map 'AuditLines': item 1 has an empty key
  60 lines, limit 50         -> fatal, attempts 1, lookups 0: map 'AuditLines' has 60 items, more than its limit of 50

=== Scenario 3a: an unknown SKU (fatal) -- Retry(2) on the map, Retry(1) on CheckPrice inside the child ===
  Failed at 'AuditLines' after 1 attempt(s), fatal:
    item 'L03': failed at 'CheckPrice' after 1 attempt(s): unknown SKU 'INK-X9'
  Fatal in the child, fatal for the item, fatal for the map: neither retry policy ran.

=== Scenario 3b: the price API is busy (transient, retry in 2 s) -- one request at a time ===
  No retry on the map: failed, transient, strata.retry_after_ms = 2000:
    item 'L02': failed at 'CheckPrice' after 2 attempt(s): price service busy (429)
  Lookups: L01, L02, L02 -- L03 never started.
  With Retry(1) on the map: approve. MapExpanded on attempts 1 and 2.
  Lookups: L01, L02, L02, L01, L02, L03 -- the retry re-ran L01, which had succeeded.

=== Scenario 4: Partial = KeepSucceeded -- audit what can be audited, flag the rest ===
  Reducer saw (list order): L01 Succeeded, L02 Succeeded, L03 Failed, L04 Succeeded, L05 Succeeded; JoinMet: False
  L03's error: Fatal, "failed at 'CheckPrice' after 1 attempt(s): unknown SKU 'INK-X9'"
  Decision: hold: 2 line(s) need review
    - L03 not audited: failed at 'CheckPrice' after 1 attempt(s): unknown SKU 'INK-X9'
    - L05 billed 52.00, list 45.00 (+16%)

=== Step 5: a child graph that could pause for a human is refused at .Compile() ===
  Refused: map 'AuditLines' runs a graph with an interrupt boundary at 'Score'; a map item can't freeze
  Refused: map 'AuditLines' runs a graph with an interrupt boundary at 'Review/Score'; a map item can't freeze
  MaxConcurrency = 0 is refused too (ArgumentOutOfRangeException): a map is never unbounded.

=== Done: one child graph per line, bounded, keyed, and failing the way its children failed. ===
```

The demo checks each claim it prints (keys, start order, peaks, paths,
messages, error kinds, retry hints, lookup sequences, statuses) and exits
non-zero if one fails.

## Files

- `Program.cs` — the whole demo: the scenarios as top-level statements, the
  parent and child graphs, a `PriceList` that records lookups and their peak
  in flight, and a `Recorder` run observer
- `18-map-fan-out.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet
