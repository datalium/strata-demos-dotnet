# 14 — graph-ci-gate

`strata graph validate` as a **CI gate** (STRATA-SPEC-004's other entry point).
`.Compile()` already rejects a structurally broken graph — but only when the
program *runs*. A pipeline wants the same verdict from a file, without running
anything: `strata graph validate --path graphs/`. This demo builds seven graphs
in code, exports them to JSON, and checks the folder with the real `strata`
binary.

Four of the graphs are healthy — `order-pipeline` (its `ManualReview` node is
reachable only through a recovery policy's fallback), `document-review` (a
guarded retry loop), `support-desk` (a branch that rejoins) and
`payout-approval` (an interrupt boundary whose reject route leads to a node
nothing else enters). Three each break a different rule: `refund-flow`
(unguarded cycle), `report-builder` (orphan node), `import-job` (entry point
names a node that doesn't exist).

Nothing here ever *runs* a graph; only their shape matters, so every graph uses
one trivial state type.

## What it demonstrates

- **`StateGraph<T>.ToDefinition()` — export without validating.** The way from
  code to the CLI's file (`using Datalium.Strata.Storage.Schema;`). `.Compile()`
  can't be the export step: it throws on a broken graph, and a broken graph is
  what a gate exists to catch. The demo writes all seven to `graphs/*.json`
  *before* compiling any of them
- **Scenario 1 — a broken pull request.** One call, `strata graph validate
  --path graphs`, checks every `*.json` under the folder, reports each file
  (`OK` / `FAILED`) with its errors, and exits **1**. Then a table asserting that
  for every graph the CLI and `.Compile()` return the *same* diagnostics
- **Scenario 2 — a trap the export avoids.** A compiled graph's `Topology` holds
  only the edges the *executor* routes over. It has no edge into `ManualReview`,
  so the CLI would call it an orphan of a graph `.Compile()` accepted.
  `ToDefinition()` (on the builder or the compiled graph) adds the fallback as a
  guarded `on_failure` edge, so both agree
- **Scenario 3 — an interrupt boundary in the file** (`design/48-Boundaries-In-Definitions.md`).
  `payout-approval` marks `Approve` with `.AddInterruptBoundary("Approve",
  onReject: "Refuse")`. The export writes `"interrupt": { "timeout_ms": null }`
  on that node, the reject route as a guarded `on_reject` edge, and the level as
  `schema_version` `1.6.0` (the other six stay `1.0.0`). `strata graph view`
  prints the boundary and where a rejection goes
- **Scenario 4 — the fix.** Three small changes (guard the retry edge, wire
  `Archive` in, fix the entry point), same seven files: exit **0**, and all
  seven really do compile
- **Scenario 5 — when the gate can't check.** A `.json` file that isn't a graph
  definition, a hand edit that drops `Approve`'s `interrupt` but keeps its
  `on_reject` edge (a shape no engine writes; .NET's
  `GraphDefinition.ToTopology()` refuses it with the same message), an empty
  folder, a mistyped path: all exit **2**. A gate that couldn't look at
  something must not read as a pass
- **Exit codes:** `0` every graph valid · `1` structural errors · `2` could not
  check
- **Cross-language.** `--out <dir>` keeps the final export. The Rust twin
  ([`strata-demos-rust/demos/14-graph-ci-gate`](https://github.com/datalium/strata-demos-rust/tree/main/demos/14-graph-ci-gate))
  has all seven graphs and writes **byte-identical** files for them
  (checked against Strata 0.9: `--out` on both sides, then `diff -r`)

## What it does *not* cover

- **Composite nodes, joins, maps, waits, identity.** These graphs are flat. A
  definition can carry much more: parallel groups with their branch names and
  subgraphs with the child's whole definition (`1.1.0`), joins (`1.2.0`), maps,
  waits, graph identity, and boundaries on any of them. The validator recurses
  into them and names a child's errors by path (`Remediate/Abort`)
- **A boundary's timeout and `onTimeout` route.** `payout-approval` has no
  timeout, so `timeout_ms` is `null` and there is no `on_timeout` edge; waits
  and timers are demo [`19-waits-and-timers`](../19-waits-and-timers)
- **A conditional edge's predicate.** The export records that an edge is
  *guarded* — which is all the validator needs — but not what the condition is
  (the engine keeps no text for it)
- **Dangling edges** (`STRATA_ERR_DANGLING_EDGE`, an edge to a name that isn't
  a node) and the composite-node rules (`STRATA_ERR_EMPTY_GROUP`,
  `STRATA_ERR_DUPLICATE_BRANCH`, `STRATA_ERR_INVALID_QUORUM`,
  `STRATA_ERR_BOUNDARY_IN_MAP`): the gate reports them the same way, but none
  of these graphs breaks them
- **What isn't exported**: a hop limit (`MaxNodeHops`), node and branch
  timeouts, a group's concurrency limit. The gate can't see them
- **Running the graphs**, a real CI runner, or a real LLM. Everything is
  offline and deterministic

## Wiring it into CI

Your repo needs one small program (a test, a tool) that builds its graphs and
writes `graph.ToDefinition()` as JSON — this demo's `Repo.ExportAll` is that
program. Then, with the `strata` binary on the runner (it is a Rust binary
even for .NET projects; download it from the [GitHub Release](https://github.com/datalium/strata/releases/tag/v0.9.0-preview.1)):

```yaml
- run: |
    curl -fsSL -o strata https://github.com/datalium/strata/releases/download/v0.9.0-preview.1/strata-linux-x64
    chmod +x strata && sudo mv strata /usr/local/bin/strata
- run: dotnet run --project tools/ExportGraphs -- graphs/   # writes graphs/*.json via ToDefinition()
- run: strata graph validate --path graphs                  # non-zero exit fails the job
```

## Run it

```bash
dotnet run --project demos/14-graph-ci-gate/14-graph-ci-gate.csproj
dotnet run --project demos/14-graph-ci-gate/14-graph-ci-gate.csproj -- --out ./graphs-dotnet   # also keep the final export
```

The `strata` CLI is a **Rust** binary, not a NuGet package. Download the one
for your platform from the [`v0.9.0-preview.1` release](https://github.com/datalium/strata/releases/tag/v0.9.0-preview.1)
(`strata-linux-x64`, `strata-macos-x64` or `strata-windows-x64.exe`; check it
against `SHA256SUMS.txt`) and either put it on your `PATH` as `strata`
(`strata.exe` on Windows; `chmod +x` it on Linux and macOS) or set
`STRATA_BIN=/path/to/strata`. No Rust toolchain is needed. The demo prints
which binary it found and its version, and stops with a pointer to the
release if it finds none.

Expected output, from a run on Windows (the first line names the binary the
demo found, so its path is yours; paths print with `/` on Linux and macOS; the
last line of scenario 5 quotes the operating system's own error text; blank lines inside the indented CLI output are dropped):

```
Using strata CLI: C:\tools\strata.exe (strata 0.9.0-preview.1)

== 1. A broken pull request: seven graphs exported, one CLI call ==

$ strata graph validate --path graphs
  OK      graphs\document-review.json
  FAILED  graphs\import-job.json  (1 structural error(s))
    [STRATA_ERR_MISSING_ENTRY] Load: Entry point node 'Load' does not exist in the graph definition.
  OK      graphs\order-pipeline.json
  OK      graphs\payout-approval.json
  FAILED  graphs\refund-flow.json  (1 structural error(s))
    [STRATA_ERR_UNGUARDED_CYCLE] Approve -> Request: Unguarded cycle detected between 'Approve' and 'Request'. Add a guarded/conditional edge to protect this loop.
  FAILED  graphs\report-builder.json  (1 structural error(s))
    [STRATA_ERR_ORPHAN] Archive: Node 'Archive' is isolated and unreachable from the entry point 'Collect'.
  OK      graphs\support-desk.json
  7 graph file(s) checked: 4 valid, 3 invalid, 0 unreadable.
  (exit code 1)

The CLI runs the same check as .Compile() -- same diagnostics, every graph:

  graph             .Compile()                    strata graph validate
  order-pipeline    ok                            ok
  document-review   ok                            ok
  support-desk      ok                            ok
  refund-flow       STRATA_ERR_UNGUARDED_CYCLE    STRATA_ERR_UNGUARDED_CYCLE
  report-builder    STRATA_ERR_ORPHAN             STRATA_ERR_ORPHAN
  import-job        STRATA_ERR_MISSING_ENTRY      STRATA_ERR_MISSING_ENTRY
  payout-approval   ok                            ok

== 2. Why export with ToDefinition(), not the compiled Topology ==

order-pipeline's ManualReview node is reachable only through Charge's
fallback. A compiled graph's Topology holds just the edges the executor
routes over, so it has no edge into ManualReview:

  FAILED  trap\routing-edges-only.json  (1 structural error(s))
    [STRATA_ERR_ORPHAN] ManualReview: Node 'ManualReview' is isolated and unreachable from the entry point 'Validate'.
  OK      trap\to-definition.json
  2 graph file(s) checked: 1 valid, 1 invalid, 0 unreadable.

  a false alarm on a graph .Compile() accepted; ToDefinition() adds the
  fallback as a guarded `on_failure` edge, so both agree.

== 3. An interrupt boundary is in the file, with its reject route ==

payout-approval.json is schema_version 1.6.0: a level with a boundary.
The Approve node, and the route a rejection takes, as exported:

  {"name":"Approve","metadata":null,"interrupt":{"timeout_ms":null}}
  {"from":"Approve","to":"Refuse","guarded":true,"condition":"on_reject"}

$ strata graph view --path graphs/payout-approval.json
  ┌─────────┐
  │ Prepare │
  └─────────┘
       │     
  ┌─────────┐
  │ Approve │
  └─────────┘
       │     
  ┌─────────┐
  │   Pay   │
  └─────────┘
  Additional edges:
    Approve -> Refuse  (guarded: on_reject)
  Composite nodes:
    Approve: interrupt boundary, rejects to Refuse

  Refuse has no ordinary edge in. Like ManualReview, it is reachable only through
  a route, and the exported on_reject edge is what keeps it from looking orphaned.

== 4. The fix: three small changes, same seven files ==

$ strata graph validate --path graphs
  OK      graphs\document-review.json
  OK      graphs\import-job.json
  OK      graphs\order-pipeline.json
  OK      graphs\payout-approval.json
  OK      graphs\refund-flow.json
  OK      graphs\report-builder.json
  OK      graphs\support-desk.json
  7 graph file(s) checked: 7 valid, 0 invalid, 0 unreadable.
  (exit code 0)

All seven also compile -- the gate said yes to graphs the runtime accepts.

== 5. When the gate can't check something, it doesn't pass ==

a file in graphs/ that isn't a graph definition:
  OK      graphs\document-review.json
  OK      graphs\import-job.json
  ERROR   failed to parse 'graphs\notes.json' as a graph definition: missing field `schema_version` at line 1 column 29
  OK      graphs\order-pipeline.json
  OK      graphs\payout-approval.json
  OK      graphs\refund-flow.json
  OK      graphs\report-builder.json
  OK      graphs\support-desk.json
  8 graph file(s) checked: 7 valid, 0 invalid, 1 unreadable.
  (exit code 2)

a hand edit that drops Approve's `interrupt` but keeps its on_reject edge:
  ERROR   'edited\payout-approval.json' is not a graph definition: node 'Approve' has an on_reject edge but is not an interrupt boundary
  1 graph file(s) checked: 0 valid, 0 invalid, 1 unreadable.
  (exit code 2)
  .NET's GraphDefinition.ToTopology() agrees: node 'Approve' has an on_reject edge but is not an interrupt boundary

an empty folder:
  strata: no graph definition files (*.json) found under 'empty'  (exit code 2)

a mistyped path:
  strata: failed to read 'graphz': The system cannot find the file specified. (os error 2)  (exit code 2)

Exit codes: 0 every graph valid | 1 structural errors | 2 could not check.
```

The demo asserts every exit code, the exact set of failing files and error
codes, the CLI-versus-`.Compile()` agreement, the trap, the boundary in the
export and in `graph view`, and the refused hand edit, and throws if any of
them changes.

## Files

- `Program.cs` — the whole demo, top-level statements in a single file
- `14-graph-ci-gate.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `ToDefinition()`, `GraphDefinition` and `ToTopology()`) from NuGet
