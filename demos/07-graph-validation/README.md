# 07 — graph-validation

Builds the same four-node document review pipeline (`Draft` ->
`LegalReview` (first pass only) -> `Review` -> `Archive`) twice, from the
same nodes; only the edges differ. The **broken** version routes `Review`
back to `Draft` on a plain `.AddEdge(...)` with no condition, and its exit
edge names `"Archve"`, a typo. `.Compile()` throws a
`GraphCompilationException` carrying three diagnostics (dangling edge, orphan
node, unguarded cycle) before a single node runs. The **fixed** version
guards the retry edge with a real predicate and fixes the typo. It compiles
clean, even though the loop contains a branch that rejoins on plain edges,
and it runs the loop to completion.

This is the first demo to show STRATA-SPEC-004's *reject* path
(`design/Arch-Specs/STRATA-SPEC-004.md`). Every earlier demo's `.Compile()`
call ran the same validator and passed.

## What it demonstrates

- `.Compile()` runs compile-time validation and throws
  `GraphCompilationException` (its `Result.Errors` carries every
  `ValidationError`) — structural errors abort at startup, not at runtime.
  The demo catches it, prints every error's `Code` and `Message`, and checks
  that exactly the three expected errors are present
- **Dangling edge** (`STRATA_ERR_DANGLING_EDGE`): `.AddEdge("Review", "Archve")`
  names a node that doesn't exist. `AddEdge` doesn't check its arguments;
  `.Compile()` reports one error per such edge, with `Node` = the edge's
  `from` and `RelatedNode` = its `to`
- **Orphan node** (`STRATA_ERR_ORPHAN`): the same typo leaves `Archive`
  unreachable from the entry point. A dangling edge is left out of the
  reachability walk, so one typo produces both errors, and the dangling-edge
  error says where the mistake is
- **Unguarded cycle** (`STRATA_ERR_UNGUARDED_CYCLE`): a plain `.AddEdge(...)`
  closing a loop (`Review` -> `Draft`) with nothing bounding how many times
  it can fire
- The rule for loops: a graph is rejected only if its **plain** edges alone
  form a cycle. The fixed graph has two plain paths from `Draft` to `Review`
  (through `LegalReview`, and directly), so it holds two cycles back to
  `Draft`, and both go through the one guarded edge
  `.AddConditionalEdge("Review", "Draft", state => state.Revision < 3)`. The
  runtime predicate, not the static shape, is what bounds the loop
- Fixing everything without changing the nodes: guarding the retry edge, and
  spelling `Archive` right so the plain `.AddEdge("Review", "Archive")` after
  it is the loop's "else" exit (same declaration-order rule as
  [`04-multi-node-graph`](../04-multi-node-graph))
- That the fixed graph doesn't just satisfy the validator: it runs, the
  `Draft`/`Review` loop fires three times before routing to `Archive`, and
  the recorded path shows both branches into `Review` were taken
  (`LegalReview` on the first pass, the direct edge on the next two). The
  program exits with code 1 if any of this differs

Two validator changes made since this demo was first written show here.
Before 2026-09-21 the dangling edge was skipped silently, so the broken
graph reported only the orphan and the cycle, and the fixed graph was
wrongly rejected, as `Unguarded cycle detected between 'Draft' and 'Review'`,
because the validator flagged any plain edge into a node of a guarded loop.

## What it does *not* cover

- The `strata graph validate` CLI command (STRATA-SPEC-004's other entry
  point): see [`14-graph-ci-gate`](../14-graph-ci-gate), which runs it over
  a folder of exported graphs
- A loop whose guard never turns false: the validator accepts it, because
  it can't evaluate the predicate. `Compile(new CompilationOptions { MaxNodeHops = ... })`
  stops such a thread at run time; see
  [`16-loop-circuit-breaker`](../16-loop-circuit-breaker)
- The validator's checks for parallel groups and maps (an empty group, a
  duplicate branch, an unreachable quorum, a wait or boundary inside a map):
  see [`17-parallel-joins`](../17-parallel-joins) and [`18-map-fan-out`](../18-map-fan-out)
- The spec's "missing terminal path" check is not built: a graph with no
  exit passes if it has no orphan and no unguarded cycle
- Tool-calling, streaming, interruption: see
  [`02-tool-calling`](../02-tool-calling) and the demos after this one

## Run it

```bash
dotnet run --project demos/07-graph-validation/07-graph-validation.csproj
```

Expected output:

```
Compilation rejected, as STRATA-SPEC-004 requires:
  [STRATA_ERR_DANGLING_EDGE] Edge 'Review' -> 'Archve' points at 'Archve', which is not a node in the graph.
  [STRATA_ERR_ORPHAN] Node 'Archive' is isolated and unreachable from the entry point 'Draft'.
  [STRATA_ERR_UNGUARDED_CYCLE] Unguarded cycle detected between 'Review' and 'Draft'. Add a guarded/conditional edge to protect this loop.

Same nodes, fixed: guarded the retry edge and fixed the typo -- compiles clean.
path: Draft -> LegalReview -> Review -> Draft -> Review -> Draft -> Review -> Archive
'Q3 roadmap' approved after 3 revision(s), archived: True
```

(The order of the errors is fixed by the validator, not by declaration
order: for each graph level it reports the entry point, then dangling edges,
then orphans, then unguarded cycles.)

## Files

- `Program.cs` — the whole demo, single file
- `07-graph-validation.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` (for `InMemoryCheckpointStore`) from NuGet
