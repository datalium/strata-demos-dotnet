# 24 — model-routes

Model routes (`design/46-Model-Routes.md`): `ModelRoute` in
`Datalium.Strata.AI`, a chat client over ordered candidate clients. It asks
them in order and moves on when one fails transiently, refuses, or gives an
answer the route's `Accept` rejects. A fatal fault stops it. With
`HedgeAfter`, a slow candidate gets the next one started beside it, and the
first accepted answer wins. A `ModelHealth` shared between routes skips a
provider that is cooling down or whose breaker is open. When nothing answers,
the node fails transiently with the shortest wait as its retry hint, and the
node's `RecoveryPolicy` waits that long.

Every route sits behind an ordinary model node (`AddModelNode`, which calls
`StrataChat`), so each attempt is a model call of the run, and the route
records one `ModelRouted` run event per call. The demo reads that event to
show which candidate answered and what happened to each of the others.

**The models are stand-ins.** `FakeModel` is a small scripted `IChatClient`:
each turn answers, fails with an HTTP status and an optional `Retry-After`
(as a `ModelCallException`), refuses (finish reason `content_filter`), or
hangs until it is cancelled. It names a provider and a model in its metadata,
which is where the route's default health key comes from. No network, no API
key, and every run prints the same thing.

The Rust twin is
[`strata-demos-rust/demos/24-model-routes`](https://github.com/datalium/strata-demos-rust/tree/main/demos/24-model-routes);
its README says where the two engines differ.

## What it demonstrates

- **Failover** (scenario 1). Four candidates: a 429 with `Retry-After: 30 s`
  (`Failed 429`), a refusal (`Refused`), an answer `Accept` rejects because it
  gives no reason (`Rejected`), and one that answers (`Answered`). The run has
  four `ModelCallStarted` / `ModelCallCompleted` pairs, each naming the route,
  and each `ModelRouteAttempt.CallId` pairs with one. Only the transient fault
  counts against health: northwind is `CoolingDown` with 1 failure, while the
  refusing and rejected providers stay `Available` with 0
- **A fatal fault stops the route** (scenario 2). A 401 is the request's
  fault, not the model's, so the route doesn't try the backup (`NotTried`,
  never called). The node fails with the candidate's own fault,
  `model call failed (401): ...`, after 1 attempt, although its policy is
  `Retry(3)`
- **Hedging** (scenario 3). With `HedgeAfter = 50 ms`, a candidate that
  hasn't answered gets the next one started beside it (`Hedges = 1`). The
  first accepted answer wins and the other is cancelled (`LostRace`). 3a: the
  hedge wins. 3b: the first candidate answers after the hedge has started,
  and wins. Neither depends on timing: in 3a the slow candidate hangs until it
  is cancelled, and in 3b it waits for a signal the hedge sends when it is
  asked, while the hedge hangs. The 50 ms only decides when the hedge starts
- **`Eligible`** (scenario 4) skips a candidate for one request. Here the rule
  reads the run's metadata (`plan`): a paid request is answered by `frontier`,
  a free one sees `frontier` as `Ineligible` with the rule's reason and is
  answered by `local`. The ineligible candidate isn't called
- **A shared `ModelHealth` breaker** (scenario 5). Two routes in one graph,
  `diagnose` and `summarise`, share a `ModelHealth` with
  `Breaker(Failures: 2, OpenFor: 60 s)` on a `FakeTimeProvider`. Their first
  candidates are two models of one provider (`nw-large`, `nw-small`). The
  default health key is the provider's name, so a 503 in each route makes 2
  failures in a row on one key and opens its breaker. The next run skips
  northwind in both routes (`BreakerOpen 60 s`) without calling it. After the
  clock moves 60 s the key is `HalfOpen`: one test call goes through,
  succeeds, and closes the breaker
- **Nothing answers** (scenario 6). Both candidates fail, with `Retry-After`
  8 s and 3 s. The route throws `route_exhausted`, transient, and its hint is
  the **shortest** wait (3 s), when some candidate can be asked again. The node
  fails transiently with that hint. Its policy,
  `Retry(2).WithBackoff(200 ms, 30 s)`, waits the hint instead of its own
  backoff (`RetryScheduled.Delay` = 3 s). Health persists across the retry:
  attempt 2 skips `a`, which is still cooling down, and `b` answers. The run
  and the health share a `FakeTimeProvider`. The clock stays still until the
  route has failed, so the hint is exact, and then moves until the retry runs

Each claim is checked when the demo runs, and the demo exits non-zero if one
fails.

## What it does *not* cover

- **Streams.** A streaming run (`RunStreaming`) routes a stream: it fails
  over only until its first update, and a hedged stream is won by the first
  candidate to send an update. This demo's runs don't stream. See
  [`06-token-streaming`](../06-token-streaming) for a streaming model node.
- **An agent over a route.** `StrataAgent` takes a route like any
  `IChatClient`, and a failover can happen on any turn of its tool loop.
  [`23-agent-tool-loop`](../23-agent-tool-loop) covers the agent itself.
- **Cancellation** of a route call (every attempt in flight `Cancelled`, the
  rest `NotTried`, health untouched), `OnRouted` for a route called outside a
  run, a custom `FaultClassifier`, and the `route <name>` span and
  `strata.route.*` metrics. The engine's `ModelRouteTests.cs` covers these.
- **Retries on the same candidate.** A route never asks a candidate twice in
  one call. The node's `RecoveryPolicy` retries the whole route, as scenario 6
  shows.

## Run it

```bash
dotnet run --project demos/24-model-routes/24-model-routes.csproj
```

Output of a real run (identical on every run):

```
=== Scenario 1: failover past a 429, a refusal and a rejected answer ===
  Answer: Roll back deploy 412, because the error rate rose with it.
  Model calls in the run (one per attempt, each naming the route):
    ModelCallStarted   nw-large     route=triage
    ModelCallCompleted nw-large     Failed
    ModelCallStarted   ct-guarded   route=triage
    ModelCallCompleted ct-guarded   Succeeded (content_filter)
    ModelCallStarted   fb-mini      route=triage
    ModelCallCompleted fb-mini      Succeeded (stop)
    ModelCallStarted   llama-local  route=triage
    ModelCallCompleted llama-local  Succeeded (stop)
  ModelRouted Ask#1 route 'triage': answered by local (llama-local), hedges 0
    primary   Failed       429
    careful   Refused
    terse     Rejected     it gives no reason
    local     Answered
  Health: contoso=Available(0), fabrikam=Available(0), local=Available(0), northwind=CoolingDown(1)

=== Scenario 2: a 401 is fatal -- the route stops, and Retry(3) doesn't retry it ===
  Node 'Ask' failed after 1 attempt(s): model call failed (401): 401 Unauthorized: invalid API key
  ModelRouted Ask#1 route 'fatal': nobody answered, hedges 0
    primary   Failed       401
    backup    NotTried

=== Scenario 3a: HedgeAfter 50 ms -- slow hangs, fast starts beside it and wins ===
  Answer: Roll back deploy 412, because the error rate rose with it.
  ModelRouted Ask#1 route 'hedged': answered by fast (llama-local), hedges 1
    slow      LostRace
    fast      Answered

=== Scenario 3b: the same route shape -- slow answers once the hedge has started, and still wins ===
  Answer: Roll back deploy 412, because the error rate rose with it.
  ModelRouted Ask#1 route 'hedged': answered by slow (nw-large), hedges 1
    slow      Answered
    hedge     LostRace

=== Scenario 4: Eligible -- the free plan skips the frontier candidate ===
  plan=paid:
  ModelRouted Ask#1 route 'plans': answered by frontier (nw-large), hedges 0
    frontier  Answered
    local     NotTried
  plan=free:
  ModelRouted Ask#1 route 'plans': answered by local (llama-local), hedges 0
    frontier  Ineligible   the free plan doesn't use frontier models
    local     Answered

=== Scenario 5: a shared ModelHealth with Breaker(2 failures, open 60 s) across two routes ===
  Run 1: northwind fails in each route (2 failures in a row: the breaker opens)
  ModelRouted Diagnose#1 route 'diagnose': answered by steady (llama-local), hedges 0
    large     Failed       503
    steady    Answered
  ModelRouted Summarise#1 route 'summarise': answered by steady (llama-local), hedges 0
    small     Failed       503
    steady    Answered
  Health: local=Available(0), northwind=Open(2)
  Run 2: northwind's breaker is open, so neither route asks it
  ModelRouted Diagnose#1 route 'diagnose': answered by steady (llama-local), hedges 0
    large     BreakerOpen  60 s
    steady    Answered
  ModelRouted Summarise#1 route 'summarise': answered by steady (llama-local), hedges 0
    small     BreakerOpen  60 s
    steady    Answered
  (fake clock +60 s)
  Health: local=Available(0), northwind=HalfOpen(2)
  Run 3: one test call goes through, succeeds and closes the breaker
  ModelRouted Diagnose#1 route 'diagnose': answered by large (nw-large), hedges 0
    large     Answered
    steady    NotTried
  ModelRouted Summarise#1 route 'summarise': answered by small (nw-small), hedges 0
    small     Answered
    steady    NotTried
  Health: local=Available(0), northwind=Available(0)

=== Scenario 6: nothing answers -- a transient failure whose hint is the shortest wait ===
  Attempt 1 failed: model call failed (route_exhausted): no candidate of route 'exhausted' answered: a failed (429), b failed (503)
  RetryScheduled delay: 3 s (b's Retry-After, the shorter of 8 s and 3 s; not the 200 ms backoff)
  ModelRouted Ask#1 route 'exhausted': nobody answered, hedges 0
    a         Failed       429
    b         Failed       503
  ModelRouted Ask#2 route 'exhausted': answered by b (ct-guarded), hedges 0
    a         CoolingDown  5 s
    b         Answered
  Answer: Roll back deploy 412, because the error rate rose with it.

=== Done: the route failed over, stopped on a fatal fault, hedged, skipped by rule and by health, and handed its hint to the retry. ===
```

`Health: key=State(failures)` is `ModelHealth.Snapshot()`. `Ask#2` is the
node's second attempt, the retry. Its `5 s` is how long `a`'s 8 s cooldown
still has to run, rounded up to whole seconds. The demo checks that `a` is
`CoolingDown`, but not the exact figure, since the fake clock moves in 500 ms
steps until the retry fires.

## Files

- `Program.cs` — the six scenarios, plus `FakeModel` (the scripted client),
  `Turn` (one scripted answer or fault) and `Recorder` (an `IRunObserver` that
  collects a run's events)
- `24-model-routes.csproj` — references `Datalium.Strata`,
  `Datalium.Strata.Storage` and `Datalium.Strata.AI`, plus
  `Microsoft.Extensions.TimeProvider.Testing` for the fake clock
