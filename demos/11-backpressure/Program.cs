// Backpressure (STRATA-SPEC-001): what happens when a node produces output faster than the
// consumer reads it. The spec's claim is that the stream channel is bounded at 1024 chunks and
// that a full channel *pauses the node* rather than dropping tokens or growing without limit.
// StreamContextTests prove that in isolation; this demo proves it through the real engine --
// AddStreamingNode + RunStreaming -- by running a flood node against three deliberately bad
// consumers:
//
// 1. Stalled   -- the consumer doesn't read at all. The node must freeze at exactly the channel's
//                 capacity (1023 tokens + the engine's NodeStart chunk = 1024 queued chunks),
//                 still be running, and lose nothing once the consumer finally drains.
// 2. Slow      -- the consumer reads in bursts with pauses between. The node's lead over the
//                 consumer must never exceed the capacity, for the whole run.
// 3. Abandoned -- the consumer stops reading while the node is frozen and never comes back.
//                 Cancelling the run releases the frozen node (design/17-Async-Execution.md): an
//                 async node awaiting EmitTokenAsync with its own token stops at once, and the run
//                 ends Cancelled with a resumable checkpoint. A sync node blocked in EmitToken
//                 can't see the token; the engine abandons it, and it is let go only when the
//                 run ends and the channel closes under it.
//
// The flood node is async (Func<TState, NodeContext, CancellationToken, ValueTask<TState>>) and
// emits with EmitTokenAsync(token, ct) -- the 0.9 idiom. Scenario 3 adds a sync twin for contrast.
//
// Everything is deterministic and offline: the "flood" is a loop counting up to 20 000, and every
// token carries its own sequence number so the consumer can prove none went missing or arrived
// out of order. The demo asserts on counts, not on wall-clock time.

using System.Diagnostics;
using System.Threading.Channels;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Streaming;

// Enough tokens to overflow the channel ~19 times over.
const int TotalTokens = 20_000;

const int Capacity = StreamContext.DefaultChannelCapacity;

// The engine emits a NodeStart chunk before the node body runs, and that chunk occupies one of the
// 1024 slots -- so a stalled node can only get this many tokens out before its next write blocks.
const int FrozenAt = Capacity - 1;

await ScenarioStalledAsync();
await ScenarioSlowAsync();
await ScenarioAbandonedAsync();

Console.WriteLine(
    "=== Done: the 1024-chunk channel paused the producer instead of dropping tokens, never " +
    "let it run further ahead than its capacity, and cancelling the run released it. ===");

// Builds Flood (a streaming node that emits `Total` tokens as fast as it can, token i being the
// text `i`) followed by a plain Summarize node.
//
// `progress` counts the emits that have *returned* -- a blocked emit hasn't returned yet, so this
// is how the demo watches the producer from outside while it runs. (Node state is only visible
// once the node finishes.) `progress.Exited` records how the handler's body ended.
static CompiledGraph<FloodState> BuildGraph(Progress progress, bool sync = false)
{
    var graph = new StateGraph<FloodState>();
    if (sync)
    {
        // A sync streaming handler: the engine runs it on a thread-pool thread and hands it the
        // stream directly. EmitToken blocks that thread while the channel is full, and takes no token.
        graph.AddStreamingNode("Flood", (FloodState state, IStreamContext stream) =>
        {
            try
            {
                for (var i = 0; i < state.Total; i++)
                {
                    stream.EmitToken(i.ToString());
                    progress.Increment();
                }

                progress.Exit("returned");
                return state with { Sent = progress.Value };
            }
            catch (Exception e)
            {
                progress.Exit(e.GetType().Name);
                throw;
            }
        });
    }
    else
    {
        // An async streaming handler: the stream is on NodeContext.Stream, and EmitTokenAsync awaits
        // a free slot. Passing the handler's token is what lets a cancelled run interrupt that wait.
        graph.AddStreamingNode("Flood", async (FloodState state, NodeContext context, CancellationToken ct) =>
        {
            try
            {
                for (var i = 0; i < state.Total; i++)
                {
                    await context.Stream!.EmitTokenAsync(i.ToString(), ct);
                    progress.Increment();
                }

                progress.Exit("returned");
                return state with { Sent = progress.Value };
            }
            catch (Exception e)
            {
                progress.Exit(e.GetType().Name);
                throw;
            }
        });
    }

    return graph
        .AddNode("Summarize", state => state with
        {
            Summary = $"Flood emitted {state.Sent} of {state.Total} tokens",
        })
        .AddEdge("Flood", "Summarize")
        .Compile();
}

// Reads every chunk until the channel completes. If `pace` is set, pauses for `pace.Pause` after
// every `pace.Every` tokens.
static async Task<Drained> DrainAsync(ChannelReader<StreamChunk> reader, Progress progress, (int Every, TimeSpan Pause)? pace)
{
    var started = Stopwatch.StartNew();
    int tokens = 0, nodeStarts = 0, nodeEnds = 0, maxLead = 0;
    var inOrder = true;

    await foreach (var chunk in reader.ReadAllAsync())
    {
        switch (chunk.Type)
        {
            case StreamChunkType.NodeStart:
                nodeStarts++;
                break;
            case StreamChunkType.NodeEnd:
                nodeEnds++;
                break;
            case StreamChunkType.Token:
                if (!int.TryParse(chunk.Payload.Span, out var sequence) || sequence != tokens)
                {
                    inOrder = false;
                }

                tokens++;
                maxLead = Math.Max(maxLead, Math.Max(0, progress.Value - tokens));
                if (pace is { } p && tokens % p.Every == 0)
                {
                    await Task.Delay(p.Pause);
                }

                break;
            case StreamChunkType.Error:
                throw new InvalidOperationException($"unexpected error chunk: {chunk.Payload}");
        }
    }

    return new Drained(tokens, inOrder, nodeStarts, nodeEnds, maxLead, started.Elapsed);
}

// Polls `condition` until it holds. A slow machine only delays this; it never makes the demo's
// assertions flaky.
static async Task WaitUntilAsync(string what, Func<bool> condition)
{
    var deadline = Stopwatch.StartNew();
    while (!condition())
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(10))
        {
            throw new TimeoutException($"timed out waiting for {what}");
        }

        await Task.Delay(1);
    }
}

// Awaits the graph task, but fails loudly instead of hanging if it never ends.
static async Task<GraphExecutionResult<FloodState>> EndWithinAsync(Task<GraphExecutionResult<FloodState>> completion, string what)
{
    await WaitUntilAsync(what, () => completion.IsCompleted);
    return await completion;
}

static async Task<FloodState> CompleteWithinAsync(Task<GraphExecutionResult<FloodState>> completion, string what) =>
    await EndWithinAsync(completion, what) switch
    {
        GraphExecutionResult<FloodState>.Completed completed => completed.State,
        var other => throw new InvalidOperationException($"expected Completed, got: {other}"),
    };

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task ScenarioStalledAsync()
{
    Console.WriteLine("=== Scenario 1: the consumer stalls (reads nothing) ===");
    var progress = new Progress();
    var graph = BuildGraph(progress);
    var (reader, completion) = graph.RunStreaming("stalled", FloodState.New(TotalTokens), new InMemoryCheckpointStore());

    // Deliberately do not read. Wait for the producer to run out of room, then give it a generous
    // window in which to (wrongly) make more progress.
    await WaitUntilAsync("the flood node to fill the channel", () => progress.Value >= FrozenAt);
    await Task.Delay(300);

    var frozen = progress.Value;
    Console.WriteLine(
        $"  Consumer has read nothing. The flood node wants to emit {TotalTokens} tokens; " +
        $"after 300 ms it has emitted {frozen}.");
    Console.WriteLine(
        $"  Queued in the channel: {reader.Count} chunk(s) (capacity {Capacity}) = the engine's " +
        $"NodeStart + {frozen} tokens.");
    Check(frozen == FrozenAt, $"a stalled consumer must freeze the producer at exactly capacity - 1 tokens, but it emitted {frozen}");
    Check(reader.Count == Capacity, $"the channel should be full ({Capacity}), but holds {reader.Count}");
    Check(!completion.IsCompleted, "the node must be paused, not finished or dead");
    Console.WriteLine("  The node is still running -- paused awaiting EmitTokenAsync, not dropping.");

    Console.WriteLine("  Now the consumer starts reading...");
    var drained = await DrainAsync(reader, progress, pace: null);
    var state = await CompleteWithinAsync(completion, "the graph to finish after draining");

    Console.WriteLine(
        $"  Received {drained.Tokens} tokens in order: {drained.InOrder}. Lifecycle chunks: " +
        $"{drained.NodeStarts} NodeStart, {drained.NodeEnds} NodeEnd (one pair per node).");
    Console.WriteLine($"  {state.Summary}");
    Check(drained.Tokens == TotalTokens, "tokens were dropped");
    Check(drained.InOrder, "tokens arrived out of order");
    Check(drained is { NodeStarts: 2, NodeEnds: 2 }, "expected one NodeStart/NodeEnd pair per node");
    Check(state.Sent == TotalTokens, "the node did not finish emitting");
    Console.WriteLine(
        $"  Nothing lost: pausing the node cost time, not data ({TotalTokens} tokens is " +
        $"{TotalTokens / Capacity}x the channel).\n");
}

static async Task ScenarioSlowAsync()
{
    Console.WriteLine("=== Scenario 2: the consumer is slow (reads 250 tokens, then naps 1 ms) ===");
    var progress = new Progress();
    var graph = BuildGraph(progress);
    var (reader, completion) = graph.RunStreaming("slow", FloodState.New(TotalTokens), new InMemoryCheckpointStore());

    var drained = await DrainAsync(reader, progress, pace: (250, TimeSpan.FromMilliseconds(1)));
    var state = await CompleteWithinAsync(completion, "the graph to finish");

    Console.WriteLine($"  Received {drained.Tokens} tokens in order: {drained.InOrder}, in {(long)drained.Elapsed.TotalMilliseconds} ms.");
    Console.WriteLine(
        $"  Largest lead the producer ever had over the consumer: {drained.MaxLead} tokens " +
        $"(capacity {Capacity}).");
    Console.WriteLine($"  {state.Summary}");
    Check(drained.Tokens == TotalTokens, "tokens were dropped");
    Check(drained.InOrder, "tokens arrived out of order");
    Check(
        drained.MaxLead <= Capacity,
        $"the producer got {drained.MaxLead} tokens ahead of the consumer -- the channel is not bounded");
    Check(
        drained.MaxLead >= Capacity / 2,
        "the producer never got far ahead, so this scenario proved nothing");
    Console.WriteLine(
        $"  The producer ran flat out and hit the wall over and over; it never got more than " +
        $"{Capacity} tokens ahead.\n");
}

static async Task ScenarioAbandonedAsync()
{
    Console.WriteLine("=== Scenario 3: the consumer walks away while the node is frozen ===");
    var progress = new Progress();
    var graph = BuildGraph(progress);
    var store = new InMemoryCheckpointStore();
    using var cts = new CancellationTokenSource();
    var (reader, completion) = graph.RunStreaming("abandoned", FloodState.New(TotalTokens), store, cts.Token);

    await WaitUntilAsync("the flood node to fill the channel", () => progress.Value >= FrozenAt);
    Console.WriteLine($"  The node is frozen at {FrozenAt} tokens. The consumer stops reading and never comes back...");
    await Task.Delay(300);
    Check(progress.Value == FrozenAt, "the frozen node must not make progress");
    Check(!completion.IsCompleted, "the frozen node must still be waiting");
    Console.WriteLine("  300 ms later it is still waiting in EmitTokenAsync. Nothing reads, so nothing will free a slot.");

    // A ChannelReader has nothing to drop or dispose, so walking away can't tell the node anything.
    // Cancelling the run can: the node's token cancels its pending write.
    Console.WriteLine("  The caller gives up and cancels the run's token.");
    cts.Cancel();
    var result = await EndWithinAsync(completion, "the cancelled run to end");
    if (result is not GraphExecutionResult<FloodState>.Cancelled cancelled)
    {
        throw new InvalidOperationException($"expected Cancelled, got: {result}");
    }

    await WaitUntilAsync("the flood handler to exit", () => progress.Exited is not null);
    var checkpoint = store.Load("abandoned");
    Console.WriteLine(
        $"  The run ended Cancelled ({cancelled.Reason}) at '{cancelled.Node}', without anyone reading. " +
        $"The pending emit threw {progress.Exited}, the handler exited, and it emitted nothing more ({progress.Value} tokens).");
    Console.WriteLine(
        $"  The store holds a '{checkpoint?.Status.ToString().ToLowerInvariant()}' checkpoint at '{checkpoint?.SuspendedNode}': " +
        "a resume would re-enter Flood with its starting state.");
    Check(cancelled is { Reason: CancelReason.Requested, Node: "Flood" }, "expected the run to stop at Flood because it was asked to");

    // The handler's own token cancels the write, so it sees OperationCanceledException, as code
    // that catches cancellation expects (earlier 0.9 builds could close the channel first, and the
    // write threw ChannelClosedException instead; see the 0.9.0-preview.1 CHANGELOG).
    Check(
        progress.Exited is nameof(OperationCanceledException) or nameof(TaskCanceledException),
        $"the handler should have seen its cancellation, but it exited with {progress.Exited}");
    Check(progress.Value == FrozenAt, "the cancelled node must not emit more tokens");
    Check(checkpoint is { Status: CheckpointStatus.Cancelled, SuspendedNode: "Flood" }, "expected a cancelled checkpoint at Flood");

    // What was already queued is still readable: the stream ended, it wasn't torn down.
    var leftover = await DrainAsync(reader, progress, pace: null);
    Console.WriteLine(
        $"  The {leftover.Tokens} tokens already queued are still readable; the stream ends with " +
        $"{leftover.NodeStarts} NodeStart and {leftover.NodeEnds} NodeEnd (a cancelled node gets none).");
    Check(leftover is { Tokens: FrozenAt, InOrder: true, NodeStarts: 1, NodeEnds: 0 }, "expected the queued tokens and no NodeEnd");

    // The same flood as a sync handler. EmitToken takes no token, so the cancel can't reach the
    // blocked write itself. The engine stops waiting for the handler and ends the run anyway, and
    // completing the channel when the run ends is what fails the write.
    Console.WriteLine();
    Console.WriteLine("  The same, with a sync handler blocked in EmitToken (which takes no token):");
    var syncProgress = new Progress();
    var syncGraph = BuildGraph(syncProgress, sync: true);
    using var syncCts = new CancellationTokenSource();
    var (_, syncCompletion) = syncGraph.RunStreaming(
        "abandoned-sync", FloodState.New(TotalTokens), new InMemoryCheckpointStore(), syncCts.Token);

    await WaitUntilAsync("the sync flood node to fill the channel", () => syncProgress.Value >= FrozenAt);
    syncCts.Cancel();
    var syncResult = await EndWithinAsync(syncCompletion, "the cancelled sync run to end");
    await WaitUntilAsync("the sync flood handler to exit", () => syncProgress.Exited is not null);
    var syncCancelled = syncResult as GraphExecutionResult<FloodState>.Cancelled;
    Console.WriteLine(
        $"  The run ended Cancelled ({syncCancelled?.Reason}) at '{syncCancelled?.Node}' too. The engine abandoned the " +
        $"handler, and its EmitToken failed with {syncProgress.Exited} once the run ended and closed the channel.");
    Check(syncCancelled is { Reason: CancelReason.Requested, Node: "Flood" }, $"expected the sync run to end Cancelled at Flood, got: {syncResult}");
    Check(syncProgress.Exited == nameof(ChannelClosedException), $"expected the blocked EmitToken to fail when the channel closed, but the handler exited with {syncProgress.Exited}");
    Check(syncProgress.Value == FrozenAt, "the abandoned sync node must not emit more tokens");
    Console.WriteLine("  Either way, cancelling the run frees a producer that nobody is reading.\n");
}

sealed record FloodState
{
    public required int Total { get; init; }
    public required int Sent { get; init; }
    public required string Summary { get; init; }

    public static FloodState New(int total) => new() { Total = total, Sent = 0, Summary = string.Empty };
}

// Counts the emits that have returned, readable from another thread while the node runs, and
// records how the handler's body ended (null while it runs).
sealed class Progress
{
    private int _value;
    private string? _exited;

    public int Value => Volatile.Read(ref _value);

    public string? Exited => Volatile.Read(ref _exited);

    public void Increment() => Interlocked.Increment(ref _value);

    public void Exit(string how) => Volatile.Write(ref _exited, how);
}

// What a consumer saw while draining a whole run. MaxLead is the largest number of tokens the
// producer had emitted beyond what the consumer had read, sampled every time the consumer read one.
sealed record Drained(int Tokens, bool InOrder, int NodeStarts, int NodeEnds, int MaxLead, TimeSpan Elapsed);
