# 11 — backpressure

Backpressure (STRATA-SPEC-001): what happens when a node produces output
faster than anyone reads it. The spec's claim is that the stream channel is
bounded at 1024 chunks and that a full channel **pauses the node** instead of
dropping tokens or buffering without limit.

`StreamContextTests` prove that against the bare `StreamContext`. This demo
proves it **through the real engine** — a streaming node added with
`.AddStreamingNode(...)` and run with `CompiledGraph.RunStreaming(...)`, the
same path [`06-token-streaming`](../06-token-streaming) uses — with a flood node
that emits 20 000 tokens as fast as it can (19 times the channel), read by three
deliberately bad consumers. No network and no LLM: every token is just its own
sequence number, so the consumer can prove none went missing or arrived out of
order.

## What it demonstrates

- **An async streaming node, the 0.9 idiom.** `Flood` is added with
  `.AddStreamingNode(name, async (state, context, ct) => ...)`, finds the stream
  on `NodeContext.Stream`, and emits with `EmitTokenAsync(token, ct)`, passing
  its own token (`design/17-Async-Execution.md`)
- **Scenario 1, a stalled consumer.** Nothing reads. The node freezes at
  **exactly 1023 tokens**: the channel holds 1024 chunks, and one slot is taken
  by the `NodeStart` chunk the engine emits before the node body runs. The demo
  asserts the count, that the queue holds exactly 1024 chunks, and that the node
  is still running (paused, not dead) 300 ms later. Then the consumer starts
  reading and gets **all 20 000 tokens, in order**: pausing cost time, not data
- **Scenario 2, a slow consumer.** The consumer reads 250 tokens, naps 1 ms,
  repeats. Every time it reads a token it samples how far ahead the producer is;
  the largest lead over the whole run is **≤ 1024** (and ≥ 512, so the scenario
  can't pass vacuously with a producer that never got ahead). The producer runs
  flat out and hits the wall over and over, yet nothing is dropped
- **Scenario 3, an abandoned consumer, released by cancelling the run.** The
  consumer stops reading and never comes back. A `ChannelReader` has nothing to
  drop or dispose, so walking away tells the node nothing: 300 ms later it is
  still waiting in `EmitTokenAsync`. The caller then cancels the token it passed
  to `RunStreaming`. The run ends **`Cancelled` (`Requested`) at `Flood`** at
  once, with nobody reading; the handler's pending emit throws
  `OperationCanceledException` (its own token's, as code catching cancellation
  expects) and it exits
  having emitted nothing more; the store holds a resumable **`cancelled`
  checkpoint** at `Flood`; and the 1023 tokens already queued can still be read
  (the stream ends with no `NodeEnd` for the stopped node)
- **The same with a sync handler.** `EmitToken` takes no token, so the cancel
  can't reach the blocked write itself. The run still ends `Cancelled`: the
  engine stops waiting for a handler it has given up on, ends the run, and
  closes the channel, which fails the blocked `EmitToken` with
  `ChannelClosedException`. So a sync producer is released too, but only
  because its run ended
- The whole time the run is watched from outside with a counter of emits that
  have *returned* (a blocked emit hasn't), since node state is only visible once
  the node finishes

Before 0.4, scenario 3 showed a gap: `EmitToken` took no token and runs took
no cancellation token, so a .NET producer blocked on an abandoned stream could
only be released by reading. Async handlers, `EmitTokenAsync(token, ct)` and
run cancellation closed it.

## What it does *not* cover

- **Only one producer.** A parallel group's streaming branches
  (`ParallelBranches.StreamingBranch`) write to the same channel concurrently,
  attributed to `<group>/<branch>` (demo 10's scenario 5); this demo floods
  from one node
- **Only the default capacity.** `RunStreaming` always creates the channel at
  1024; there's no way to ask for another bound through the engine, so the
  demo can only test that one number
- **Resuming the cancelled thread.** The demo checks the checkpoint is there;
  resuming a `cancelled` checkpoint is shown in
  [`15-async-and-cancellation`](../15-async-and-cancellation)
- **A deadline or a node timeout.** `RunOptions.Deadline` and `SetTimeout` stop
  a run or an attempt the same way; see demo 15
- **No throughput numbers.** The demo asserts counts and orderings, never speed
  — the elapsed time it prints depends on the consumer's naps and the OS timer
  (`Task.Delay(1)` can take much longer than 1 ms on Windows)
- **No lossy mode.** There is no drop-oldest / drop-newest policy; the channel
  is created with `BoundedChannelFullMode.Wait`

## Run it

```bash
dotnet run --project demos/11-backpressure/11-backpressure.csproj
```

Takes about two seconds. Expected output (the `ms` figure in scenario 2 will
differ, and the largest lead is 1023 or 1024 depending on timing):

```
=== Scenario 1: the consumer stalls (reads nothing) ===
  Consumer has read nothing. The flood node wants to emit 20000 tokens; after 300 ms it has emitted 1023.
  Queued in the channel: 1024 chunk(s) (capacity 1024) = the engine's NodeStart + 1023 tokens.
  The node is still running -- paused awaiting EmitTokenAsync, not dropping.
  Now the consumer starts reading...
  Received 20000 tokens in order: True. Lifecycle chunks: 2 NodeStart, 2 NodeEnd (one pair per node).
  Flood emitted 20000 of 20000 tokens
  Nothing lost: pausing the node cost time, not data (20000 tokens is 19x the channel).

=== Scenario 2: the consumer is slow (reads 250 tokens, then naps 1 ms) ===
  Received 20000 tokens in order: True, in 1229 ms.
  Largest lead the producer ever had over the consumer: 1024 tokens (capacity 1024).
  Flood emitted 20000 of 20000 tokens
  The producer ran flat out and hit the wall over and over; it never got more than 1024 tokens ahead.

=== Scenario 3: the consumer walks away while the node is frozen ===
  The node is frozen at 1023 tokens. The consumer stops reading and never comes back...
  300 ms later it is still waiting in EmitTokenAsync. Nothing reads, so nothing will free a slot.
  The caller gives up and cancels the run's token.
  The run ended Cancelled (Requested) at 'Flood', without anyone reading. The pending emit threw OperationCanceledException, the handler exited, and it emitted nothing more (1023 tokens).
  The store holds a 'cancelled' checkpoint at 'Flood': a resume would re-enter Flood with its starting state.
  The 1023 tokens already queued are still readable; the stream ends with 1 NodeStart and 0 NodeEnd (a cancelled node gets none).

  The same, with a sync handler blocked in EmitToken (which takes no token):
  The run ended Cancelled (Requested) at 'Flood' too. The engine abandoned the handler, and its EmitToken failed with ChannelClosedException once the run ended and closed the channel.
  Either way, cancelling the run frees a producer that nobody is reading.

=== Done: the 1024-chunk channel paused the producer instead of dropping tokens, never let it run further ahead than its capacity, and cancelling the run released it. ===
```

The demo asserts everything above that must not vary and throws if it changes.
When it was first written it was also run against a `Datalium.Strata`
deliberately broken to drop chunks when the channel is full
(`BoundedChannelFullMode.DropWrite`): scenario 1 failed immediately, with the
node having emitted all 20 000 tokens instead of freezing at 1023.

## Files

- `Program.cs` — the whole demo, single file
- `11-backpressure.csproj`
