# 17 — parallel-joins

Parallel group controls and join policies
(`design/26-Parallel-Group-Controls-And-Joins.md`). Demo
[10](../10-parallel-fan-out) runs every branch of a group to the end and
merges them all. This one is about the options that change *when a group is
done* and *what its reducer sees*, on a shipping desk that asks three carriers
(swiftship, parcelpro, budgetpost) to quote for a parcel: a race for the first
quote, the first quote that passes a test, two quotes then the cheaper, a
failover chain, and a tracking board that keeps whatever answered in time. Who
won, and why the others stopped, is read from the run's events.

The Rust twin is
[`strata-demos-rust/demos/17-parallel-joins`](https://github.com/datalium/strata-demos-rust/tree/main/demos/17-parallel-joins);
its README says where the two engines differ.

## What it demonstrates

- **`ParallelGroupOptions<TState>`** on `.AddParallelGroup(name, branches,
  reducer, options)` and **`.AddParallelGroupWithOutcomes(...)`**, whose reducer
  gets a `BranchOutcomes<TState>`: every branch's `BranchOutcome` (`Name`,
  `Status` = `Succeeded` / `Failed` / `TimedOut` / `Cancelled`, `State`,
  `Error`) in declaration order, plus `JoinMet`, `Deciding` and `Succeeded`
- **`Join = FirstSuccess`** (scenario 1): the first quote wins, the group
  reduces at once, and the branches still running are cancelled. Their
  `BranchCompleted` says `Cancelled` with **`Reason = LostRace`**, and their
  cancellation tokens really fire. A new **`JoinDecided { Policy, Met,
  Deciding }`** event sits between the winner's `BranchCompleted` and the
  losers'
- **A failure doesn't end a race that can still be won** (scenario 2): a
  carrier's 503 is reported as `Failed`, and the race goes on
- **`MaxConcurrency`** (scenarios 3 and 7): branches beyond the limit start in
  declaration order as slots free. With `MaxConcurrency = 1` and
  `FirstSuccess`, a group is a **failover chain**: the backup carrier is only
  asked once the preferred one failed, and a branch that never started gets
  no events and is reported to the reducer as `Cancelled`
- **`Join = FirstMatching(predicate, description)`** (scenario 4): the first
  *matching* result wins, not the first result. budgetpost answers first and
  `Succeeded`, but only `JoinDecided` shows it wasn't the winner. With no
  match, **`Partial = KeepSucceeded`** reduces over every answer with
  `JoinMet = false` (the "first good answer, else the best of all" pattern),
  and the default `FailGroup` fails the group with `no branch matched the join
  (3 succeeded)`, transiently
- **`Join = Quorum(n)`** (scenario 5): the plain reducer is handed the deciding
  states in **declaration order**, while `JoinDecided.Deciding` keeps
  **completion order**
- **Transient or fatal: can a retry still meet the join?** (scenario 6). A
  group that can't meet its join fails transiently only if `branches - fatal
  failures >= needed`. Two fatal refusals under `Quorum(2)` of 3 make it fatal,
  so `Retry(2)` never runs. One fatal refusal plus one rate limit makes it
  transient, and the retry meets the quorum. The message names every failure in
  declaration order, and the retry re-runs **every** branch, the one that had
  succeeded included: there is no per-branch retry
- **`All` + `KeepSucceeded` + `BranchTimeout` + `MaxConcurrency`** (scenario 7),
  on a `FakeTimeProvider` passed as `RunOptions.TimeProvider`: five tracking
  calls, two at a time, 45 s each. A hung carrier is `TimedOut` (a transient
  `NodeException`, `BranchCompleted` with `Reason = TimedOut`), another fails
  fatally, and the reducer still gets the three that answered. A queued branch's
  timeout starts when *it* starts: budgetpost waits 30 s for a slot and runs
  30 s, and isn't timed out. An `All` group emits no `JoinDecided`
- **Compile-time and option checks** (step 8): `Quorum(4)` on three branches is
  `STRATA_ERR_INVALID_QUORUM` from `.Compile()`, and `MaxConcurrency = 0` is
  refused by the options setter

Two details worth knowing, both visible in the code:

- The peak in flight is replayed from `BranchStarted` / `BranchCompleted`
  events, not counted inside the branches. A group stops waiting for a branch
  the moment its token fires (a timeout or a lost race) and starts the next
  one, while the abandoned branch's own code may still be unwinding. Counted
  inside the branches, scenario 7 can show 3 in flight under a limit of 2.
  A map's items wind down instead (demo [18](../18-map-fan-out))
- Every race has a fixed order without timing: a carrier answers at once,
  never (it waits for its token), or once the run's events show a named sibling
  settled in the same attempt. The output is identical on every run

## What it does *not* cover

- **The plain fan-out** (every branch, merged in declaration order, group-level
  retry and fallback): demo [10](../10-parallel-fan-out)
- **`CancelSiblingsOnFailure = false`** (under `FailGroup`, let every branch
  finish before the group fails, naming every failure), and the group's own
  `SetTimeout`, which covers the whole group and discards every result
- **A map**, a fan-out over a list read from the state at run time: demo
  [18](../18-map-fan-out)
- **The exported `join` object** (`schema_version` `1.2.0`) and `strata graph
  validate`: see demo [14](../14-graph-ci-gate) for the CI gate
- **Streaming branches** (`StreamingBranch`)
- **No live carrier.** The carriers are canned answers, so the demo runs
  offline with no keys

## Run it

```bash
dotnet run --project demos/17-parallel-joins/17-parallel-joins.csproj
```

Takes about 3 seconds, most of it scenario 7 advancing its fake clock half a
second per step, with a 1 ms real pause between steps, through about 75 s of
fake time. Output (identical on every run):

```
=== Scenario 1: FirstSuccess -- three carriers race, the first quote wins ===
  Events:
    completed Quote/parcelpro Succeeded
    decided   first_success, met, deciding [parcelpro]
    completed Quote/swiftship Cancelled LostRace
    completed Quote/budgetpost Cancelled LostRace
  Reducer saw (declaration order): swiftship Cancelled, parcelpro Succeeded, budgetpost Cancelled; JoinMet: True
  Chosen: parcelpro EUR 14.20, 3 day(s) (parcelpro decided the join)
  swiftship and budgetpost both saw their cancellation token fire.

=== Scenario 2: swiftship errors first -- the race goes on ===
  Events:
    completed Quote/swiftship Failed -- 503 from the rating API
    completed Quote/parcelpro Succeeded
    decided   first_success, met, deciding [parcelpro]
    completed Quote/budgetpost Cancelled LostRace
  Chosen: parcelpro EUR 14.20, 3 day(s)

=== Scenario 3: MaxConcurrency = 1 + FirstSuccess -- ask in order of preference, stop at the first answer ===
  Events:
    started   Quote/swiftship
    completed Quote/swiftship Failed -- 503 from the rating API
    started   Quote/parcelpro
    completed Quote/parcelpro Succeeded
    decided   first_success, met, deciding [parcelpro]
  Reducer saw (declaration order): swiftship Failed, parcelpro Succeeded, budgetpost Cancelled; JoinMet: True
  Carriers asked: swiftship, parcelpro (peak in flight: 1). budgetpost was never called and has no events.

=== Scenario 4a: FirstMatching("delivered within 2 days") -- the first quote isn't good enough ===
  Events:
    completed Quote/budgetpost Succeeded
    completed Quote/swiftship Succeeded
    decided   first_matching, met, deciding [swiftship]
    completed Quote/parcelpro Cancelled LostRace
  Chosen: swiftship EUR 24.50, 1 day(s). budgetpost answered first and Succeeded, but only JoinDecided says it didn't win.

=== Scenario 4b: no quote matches -- Partial = KeepSucceeded reduces over every answer ===
  decided   first_matching, met: no, deciding []
  Reducer saw (declaration order): swiftship Succeeded, parcelpro Succeeded, budgetpost Succeeded; JoinMet: False
  Chosen: swiftship EUR 19.80, 3 day(s) (no quote met the join: the fastest that answered)

=== Scenario 4c: the same miss under the default Partial = FailGroup ===
  Failed: no branch matched the join (3 succeeded) -- transient (a retry asks again, and quotes change)

=== Scenario 5: Quorum(2) -- wait for two quotes, take the cheaper ===
  Events:
    completed Quote/budgetpost Succeeded
    completed Quote/parcelpro Succeeded
    decided   quorum, met, deciding [budgetpost, parcelpro]
    completed Quote/swiftship Cancelled LostRace
  The plain reducer was handed: [parcelpro, budgetpost] -- declaration order, not completion order.
  Chosen: budgetpost EUR 6.90, 5 day(s)

=== Scenario 6a: Quorum(2), two carriers refuse for good -- Retry(2) can't help ===
  Failed after 1 attempt(s), fatal:
    quorum of 2 not reached: 1 of 3 branches succeeded; branch 'swiftship': parcels over 2 kg not accepted; branch 'budgetpost': no service to postcode 10115
  3 branches - 2 fatal failures = 1, fewer than the 2 needed, so no retry was made.

=== Scenario 6b: one refuses for good, one is rate limited once -- Retry(2) meets the quorum ===
  Attempt 1 failed transiently (3 branches - 1 fatal = 2, enough for the quorum):
    quorum of 2 not reached: 1 of 3 branches succeeded; branch 'swiftship': parcels over 2 kg not accepted; branch 'budgetpost': rate limited
  Attempt 2 met the quorum, deciding [parcelpro, budgetpost].
  Calls -- swiftship: 2, parcelpro: 2, budgetpost: 2. The retry re-ran parcelpro too: there is no per-branch retry.

=== Scenario 7: tracking five parcels -- 2 at a time, 45 s each, keep what answered ===
  Started, in declaration order: swiftship, parcelpro, budgetpost, northline, coastal; peak in flight: 2.
  Tracked:
    - parcelpro: out for delivery
    - budgetpost: in transit, Leipzig hub
    - coastal: delivered
  Gaps:
    - swiftship: TimedOut -- branch 'swiftship' timed out after 45000 ms
    - northline: Failed -- unknown tracking number NL-77Q
  JoinMet: False (an All join with failures), and no JoinDecided event: an All group never emits one.
  budgetpost waited 30 s for a slot, then ran 30 s: its 45 s timeout starts when it starts, so it wasn't charged for the wait.

=== Step 8: a quorum the group can't reach is refused at .Compile() ===
  STRATA_ERR_INVALID_QUORUM at 'Quote': Parallel group 'Quote' has a quorum of 4, which isn't between 1 and its 3 branches.
  MaxConcurrency = 0 is refused by the options themselves (ArgumentOutOfRangeException).

=== Done: every join decided as it should, and the events said who won and why the rest stopped. ===
```

The demo checks each claim it prints (event order, statuses, `Deciding`,
messages, error kinds, call counts, peak in flight) and exits non-zero if one
fails.

## Files

- `Program.cs` — the whole demo: the scenarios as top-level statements, a
  `Desk` that builds each scenario's carriers and runs it, and a `Timeline`
  run observer that records events and lets a branch wait for one
- `17-parallel-joins.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet, plus `Microsoft.Extensions.TimeProvider.Testing`
  for the fake clock
