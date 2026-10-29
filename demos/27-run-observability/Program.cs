// Run observability (design/18-Run-Observability.md): run metadata, run events and telemetry.
//
// Three ways to see what a run did, all fed by the same engine hooks:
//
//   1. Run metadata   -- RunOptions.Metadata (tenant, ticket) is stamped on every checkpoint the run
//                        writes, at every nesting level, and a resume carries it forward, overriding
//                        it key by key. The engine's own keys are reserved and refused.
//   2. Run events     -- an observer (here RunEventChannel) gets typed RunEvents across a run with a
//                        retry and an interrupt, then across the resume: each with run id, sequence,
//                        node path and attempt.
//   3. Bad observers  -- one that throws and one that is stuck: neither fails nor stalls the run.
//   4. Telemetry      -- the Datalium.Strata ActivitySource and Meter, read with a plain
//                        ActivityListener / MeterListener: the strata.run -> strata.node ->
//                        strata.attempt span tree, a handler tagging its own span through
//                        NodeContext.Activity, and a few metrics.
//
// Everything is deterministic: the "transient failure" is a counter, and the demo prints counts,
// order and tags, never durations.

using System.Diagnostics;
using Datalium.Strata.Demos.RunObservability;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;

const string HandlerSourceName = "Datalium.Strata.Demos.RunObservability";
using var handlerSource = new ActivitySource(HandlerSourceName);
var patience = TimeSpan.FromSeconds(10); // a safety net so a broken claim fails instead of hanging

// --- Scenario 1: run metadata on every checkpoint, at every level, through a resume. ---

Console.WriteLine("=== Scenario 1: run metadata on every checkpoint ===");
var seenBySend = new List<IReadOnlyDictionary<string, string>>();
var orderGraph = OrderGraph(seenBySend);
var store = new InMemoryCheckpointStore();

var first = await orderGraph.RunAsync(
    "order-1001", Order.New(5), store, new RunOptions { Metadata = { ["tenant"] = "acme", ["ticket"] = "T-1001" } });
Check(first is GraphExecutionResult<Order>.Interrupted, $"expected the run to stop at Work/Approve, got {first}");
Console.WriteLine("  Run with tenant=acme, ticket=T-1001: interrupted at Work/Approve.");

var resumed = await orderGraph.ResumeAsync(
    "order-1001", HumanSignal.Approve("reviewer-7"), store, new RunOptions { Metadata = { ["ticket"] = "T-1002" } });
Check(resumed is GraphExecutionResult<Order>.Completed, $"expected the resume to complete, got {resumed}");
Console.WriteLine("  Resumed with ticket=T-1002 only: completed.");
Console.WriteLine($"  Work/Send saw NodeContext.Metadata = {Show(seenBySend.Single())}");
Check(
    Show(seenBySend.Single()) == "tenant=acme, ticket=T-1002",
    "expected the resume to carry tenant forward and override ticket");

// The graph is compiled with CheckpointPolicy.EveryNode, so the log holds a checkpoint per node.
var log = (await store.GetLogAsync("order-1001")).Entries;
Console.WriteLine($"  The thread's checkpoint log ({log.Count} checkpoints; '-> child' is the subgraph's nested checkpoint):");
foreach (var entry in log)
{
    var checkpoint = entry.Checkpoint;
    Console.WriteLine(
        $"    {entry.Sequence,2}. {checkpoint.Status,-11} next={checkpoint.NextNode ?? "-",-8} {Show(checkpoint.Metadata)}");
    if (checkpoint.Nested is { } nested)
    {
        Console.WriteLine($"          -> child {nested.Status,-11} next={nested.NextNode ?? "-",-8} {Show(nested.Metadata)}");
    }
}

Console.WriteLine("  (strata.hops is the engine's own key -- the hop count it resumes with -- which is why the strata. prefix is reserved.)");
var everyLevel = log.SelectMany(e => Levels(e.Checkpoint)).ToList();
Check(everyLevel.Count > log.Count, "expected some checkpoints to have a nested level");
Check(
    everyLevel.All(c => c.Metadata?.GetValueOrDefault("tenant") == "acme"),
    "expected tenant=acme on every checkpoint at every level");
Check(
    everyLevel.Select(c => c.Metadata!["ticket"]).Distinct().SequenceEqual(["T-1001", "T-1002"]),
    "expected ticket T-1001 before the resume and T-1002 after it");

foreach (var reserved in new[] { "error", "attempts", "strata.trace_id" })
{
    try
    {
        await orderGraph.RunAsync("order-1002", Order.New(1), store, new RunOptions { Metadata = { [reserved] = "x" } });
        Check(false, $"expected the reserved key '{reserved}' to be refused");
    }
    catch (ArgumentException refused)
    {
        Console.WriteLine($"  Metadata key '{reserved}': refused -- {refused.Message.Split(" (Parameter")[0]}");
    }
}

Check(store.Load("order-1002") is null, "expected a refused run to write nothing");
Console.WriteLine("  Nothing was written for the refused runs.");

// --- Scenario 2: run events across a run (retry, interrupt) and its resume. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: run events across a retry, an interrupt and a resume ===");
var reviewGraph = ReviewGraph(handlerSource);
store = new InMemoryCheckpointStore();

var runChannel = new RunEventChannel();
var run = await reviewGraph.RunAsync(
    "order-2001", Order.New(40), store, new RunOptions { Metadata = { ["tenant"] = "acme" }, Observer = runChannel });
var runEvents = await Drain(runChannel);

// A caller can supply the run id (a job id, a request id); otherwise the engine generates a UUID v7.
var resumeChannel = new RunEventChannel();
var resume = await reviewGraph.ResumeAsync(
    "order-2001", HumanSignal.Approve("reviewer-7"), store, new RunOptions { RunId = "resume-order-2001", Observer = resumeChannel });
var resumeEvents = await Drain(resumeChannel);
Check(run is GraphExecutionResult<Order>.Interrupted && resume is GraphExecutionResult<Order>.Completed, "expected interrupt, then complete");

PrintEvents("RunAsync", runEvents);
PrintEvents("ResumeAsync", resumeEvents);

Check(
    runEvents.Select(Describe).SequenceEqual(
    [
        "RunStarted Run at Classify",
        "NodeStarted Classify#1",
        "RetryScheduled Classify#1",
        "NodeCompleted Classify#2 Committed",
        "NodeStarted Approve#1",
        "NodeCompleted Approve#1 Committed",
        "CheckpointSaved Interrupted",
        "Interrupted at Approve, next Execute",
        "RunCompleted Interrupted",
    ]),
    "expected the documented event sequence for the run");
Check(
    resumeEvents.Select(Describe).SequenceEqual(
    [
        "RunStarted Resume at Execute",
        "CheckpointSaved Interrupted (resume stamp)",
        "Resumed by reviewer-7",
        "NodeStarted Execute#1",
        "NodeCompleted Execute#1 Committed",
        "RunCompleted Completed",
    ]),
    "expected the documented event sequence for the resume");
foreach (var events in new[] { runEvents, resumeEvents })
{
    Check(events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, events.Count).Select(i => (long)i)), "expected gapless sequences");
    Check(events.All(e => e.RunId == events[0].RunId && e.ThreadId == "order-2001"), "expected one run id per run");
}

Check(IsUuidV7(runEvents[0].RunId), "expected a generated UUID v7 run id");
Check(resumeEvents[0].RunId == "resume-order-2001", "expected the supplied run id on the resume's events");
var retry = runEvents.OfType<RetryScheduled>().Single();
Check(retry is { Error: "gateway timeout", Attempt: 1, Delay.Ticks: 0 }, "expected the retry to name the failed attempt and its error");
Check(
    ((RunStarted)resumeEvents[0]).Metadata.GetValueOrDefault("tenant") == "acme",
    "expected the resume's RunStarted to carry the stored metadata");

// --- Scenario 3: a throwing observer and a stuck observer. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: an observer can't fail or stall the run ===");
var thrower = new ThrowsOnFirstEvent();
using (var metrics = new MetricCollector())
{
    var result = await ReviewGraph(handlerSource)
        .RunAsync("order-3001", Order.New(1), new InMemoryCheckpointStore(), new RunOptions { Observer = thrower })
        .WaitAsync(patience);
    await thrower.Done.Task.WaitAsync(patience);
    var errors = metrics.Of("strata.observer.errors").Sum(m => m.Value);
    Console.WriteLine($"  Throwing observer: the run ended {Outcome(result)}; the observer threw on RunStarted,");
    Console.WriteLine($"    still got the other {thrower.Seen.Count} events, and strata.observer.errors counted {errors}.");
    Check(result is GraphExecutionResult<Order>.Interrupted, "expected the run to be unaffected by a throwing observer");
    Check(thrower.Seen.Count == runEvents.Count - 1 && thrower.Seen.First() is NodeStarted, "expected every event after the one that threw");
    Check(errors == 1, "expected the observer error to be counted once");
}

using (var gate = new ManualResetEventSlim())
{
    var stuck = new StuckObserver(gate);
    var result = await ReviewGraph(handlerSource)
        .RunAsync("order-3002", Order.New(1), new InMemoryCheckpointStore(), new RunOptions { Observer = stuck })
        .WaitAsync(patience);
    var seenWhileStuck = stuck.Seen.Count;
    Console.WriteLine($"  Stuck observer: RunAsync returned ({Outcome(result)}) while the observer had taken in {seenWhileStuck} events.");
    gate.Set();
    await stuck.Done.Task.WaitAsync(patience);
    Console.WriteLine($"    Once unblocked, it received all {stuck.Seen.Count}, in sequence order.");
    Check(seenWhileStuck == 0, "expected the run to finish while the observer was still stuck on its first event");
    Check(
        stuck.Seen.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, runEvents.Count).Select(i => (long)i)),
        "expected the stuck observer to get every event, in order, once released");
}

// --- Scenario 4: telemetry from the same hooks. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: telemetry -- the span tree and the metrics ===");
using var spans = new SpanCollector(HandlerSourceName);
using var meters = new MetricCollector();
store = new InMemoryCheckpointStore();
var telemetryGraph = ReviewGraph(handlerSource);
await telemetryGraph.RunAsync(
    "order-4001", Order.New(40), store, new RunOptions { Metadata = { ["tenant"] = "acme" }, RunId = "run-4001" });
await telemetryGraph.ResumeAsync("order-4001", HumanSignal.Approve("reviewer-7"), store, new RunOptions { RunId = "resume-4001" });

var runTrace = spans.Trace("order-4001", "strata.run");
var resumeTrace = spans.Trace("order-4001", "strata.resume");
Console.WriteLine("  RunAsync's trace (display name [operation name] tags):");
PrintTree(runTrace);
Console.WriteLine("  ResumeAsync's trace (a separate trace: there is no link across the freeze):");
PrintTree(resumeTrace);

var root = runTrace.Single(a => a.Parent is null && a.ParentSpanId == default);
var classify = runTrace.Single(a => a.DisplayName == "Classify");
var attempt1 = runTrace.Single(a => a.DisplayName == "Classify #1");
var attempt2 = runTrace.Single(a => a.DisplayName == "Classify #2");
var ownSpans = runTrace.Where(a => a.DisplayName == "classify-model-call").ToList();
Check(root.DisplayName == "strata.run" && (string?)root.GetTagItem("strata.metadata.tenant") == "acme", "expected the metadata on the run span");
Check(classify.OperationName == "strata.node" && classify.ParentSpanId == root.SpanId, "expected the node span under the run span");
Check(
    attempt1.OperationName == "strata.attempt" && attempt1.ParentSpanId == classify.SpanId && attempt2.ParentSpanId == classify.SpanId,
    "expected both attempts under the node span");
Check(attempt1.Status == ActivityStatusCode.Error && (string?)attempt1.GetTagItem("error.type") == "transient", "expected the failed attempt marked");
Check((string?)attempt2.GetTagItem("order.risk") == "low", "expected the handler's tag on its own attempt span");
Check(
    ownSpans.Count == 2 && ownSpans.Select(s => s.ParentSpanId).SequenceEqual(new[] { attempt1, attempt2 }.Select(a => a.SpanId)),
    "expected the handler's own spans under their attempts");
Check(resumeTrace.Any(a => a.DisplayName == "Execute #1") && resumeTrace.All(a => a.TraceId != root.TraceId), "expected the resume in its own trace");

Console.WriteLine("  Metrics recorded during that run and resume:");
var started = meters.Of("strata.graphs.started").GroupBy(m => m.Tag("strata.run.mode")).ToDictionary(g => g.Key, g => g.Sum(m => m.Value));
Console.WriteLine($"    strata.graphs.started      run={started.GetValueOrDefault("run")} resume={started.GetValueOrDefault("resume")}");
var retries = meters.Of("strata.node.retries");
foreach (var m in retries)
{
    Console.WriteLine($"    strata.node.retries        {m.Value} at {m.Tag("strata.node.path")} (error.type={m.Tag("error.type")})");
}

var transitions = meters.Of("strata.graph.transition");
Console.WriteLine(
    $"    strata.graph.transition    {string.Join(", ", transitions.Select(m => $"{m.Tag("strata.node.path")} -> {m.Tag("strata.next_node.path")}"))}");
var durations = meters.Of("strata.node.duration");
Console.WriteLine(
    $"    strata.node.duration       {durations.Count} measurements: {string.Join(", ", durations.Select(m => $"{m.Tag("strata.node.path")} ({m.Tag("strata.node.result")})"))}");
var checkpoints = meters.Of("strata.checkpoint.latency");
Console.WriteLine(
    $"    strata.checkpoint.latency  {string.Join(", ", checkpoints.Select(m => $"{m.Tag("strata.checkpoint.operation")} {m.Tag("strata.checkpoint.status")}"))}");

Check(started.GetValueOrDefault("run") == 1 && started.GetValueOrDefault("resume") == 1, "expected one run and one resume started");
Check(retries.Count == 1 && retries[0].Tag("strata.node.path") == "Classify", "expected one retry, at Classify");
Check(transitions.Any(m => m.Tag("strata.node.path") == "Classify" && m.Tag("strata.next_node.path") == "Approve"), "expected the Classify -> Approve transition");
Check(
    durations.Select(m => m.Tag("strata.node.path")).SequenceEqual(["Classify", "Approve", "Execute"]),
    "expected one node duration per node, retries included in the node's");

Console.WriteLine();
Console.WriteLine("=== Done: metadata on every checkpoint, typed events per run, and spans and metrics from the same hooks. ===");

// Prepare -> Work (a subgraph: Draft -> Approve, a boundary -> Send) -> Finish, with a checkpoint
// after every node so the log shows the metadata on each.
static CompiledGraph<Order> OrderGraph(List<IReadOnlyDictionary<string, string>> seenBySend)
{
    var child = new StateGraph<Order>()
        .AddNode("Draft", o => o.Then("draft"))
        .AddNode("Approve", o => o.Then("approve"))
        .AddInterruptBoundary("Approve")
        .AddNode("Send", (Order o, NodeContext ctx, CancellationToken _) =>
        {
            seenBySend.Add(ctx.Metadata);
            return ValueTask.FromResult(o.Then("send"));
        })
        .AddEdge("Draft", "Approve")
        .AddEdge("Approve", "Send")
        .Compile();
    return new StateGraph<Order>()
        .AddNode("Prepare", o => o.Then("prepare"))
        .AddSubgraph("Work", child, o => o, (_, o) => o)
        .AddNode("Finish", o => o.Then("finish"))
        .AddEdge("Prepare", "Work")
        .AddEdge("Work", "Finish")
        .Compile(new CompilationOptions { Checkpointing = CheckpointPolicy.EveryNode });
}

// Classify (fails transiently once, Retry(1)) -> Approve (a boundary) -> Execute. Classify opens a span
// of its own (as a model client would) and tags its attempt's span through NodeContext.Activity.
static CompiledGraph<Order> ReviewGraph(ActivitySource handlerSource)
{
    var calls = 0;
    return new StateGraph<Order>()
        .AddFallibleNode("Classify", (Order o, NodeContext ctx, CancellationToken _) =>
        {
            using (handlerSource.StartActivity("classify-model-call"))
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw NodeException.Transient("gateway timeout");
                }
            }

            ctx.Activity?.SetTag("order.risk", o.Amount > 100 ? "high" : "low");
            return ValueTask.FromResult(o.Then("classify"));
        })
        .AddNode("Approve", o => o.Then("approve"))
        .AddInterruptBoundary("Approve")
        .AddNode("Execute", o => o.Then("execute"))
        .AddEdge("Classify", "Approve")
        .AddEdge("Approve", "Execute")
        .SetRecoveryPolicy("Classify", RecoveryPolicy.Retry(1))
        .Compile();
}

static IEnumerable<Checkpoint> Levels(Checkpoint checkpoint)
{
    for (Checkpoint? level = checkpoint; level is not null; level = level.Nested)
    {
        yield return level;
    }
}

static async Task<List<RunEvent>> Drain(RunEventChannel channel)
{
    var events = new List<RunEvent>();
    await foreach (var e in channel.Reader.ReadAllAsync())
    {
        events.Add(e);
    }

    return events;
}

static void PrintEvents(string call, List<RunEvent> events)
{
    var runId = IsUuidV7(events[0].RunId) ? "a generated UUID v7" : events[0].RunId;
    Console.WriteLine($"  {call} -- thread {events[0].ThreadId}, run id {runId}:");
    foreach (var e in events)
    {
        Console.WriteLine($"    #{e.Sequence} {Describe(e)}{Detail(e)}");
    }
}

// The kind, node path and attempt of an event: what the checks compare.
static string Describe(RunEvent e) => e switch
{
    RunStarted s => $"RunStarted {s.Mode} at {s.Node}",
    Resumed r => $"Resumed by {r.Reviewer}",
    NodeStarted => $"NodeStarted {e.NodePath}#{e.Attempt}",
    RetryScheduled => $"RetryScheduled {e.NodePath}#{e.Attempt}",
    NodeCompleted n => $"NodeCompleted {e.NodePath}#{e.Attempt} {n.Result}",
    CheckpointSaved c => $"CheckpointSaved {c.Status}{(c.ResumeStamp ? " (resume stamp)" : "")}",
    Interrupted i => $"Interrupted at {string.Join('/', i.WaitingAt)}, next {i.NextNode}",
    RunCompleted c => $"RunCompleted {c.Outcome}",
    _ => e.GetType().Name,
};

// What else an event carries, for printing only (durations are left out: they vary).
static string Detail(RunEvent e) => e switch
{
    RunStarted s when s.Metadata.Count > 0 => $"  metadata: {Show(s.Metadata)}",
    RetryScheduled r => $"  error: {r.Error}, delay: {r.Delay.TotalMilliseconds} ms",
    Resumed r => $"  from {r.From} at {string.Join('/', r.WaitingAt)}, approved: {r.IsApproved}",
    _ => "",
};

static void PrintTree(List<Activity> trace)
{
    void Print(Activity span, int depth)
    {
        Console.WriteLine($"    {new string(' ', depth * 2)}{span.DisplayName} [{span.OperationName}]{Tags(span)}");
        foreach (var child in trace.Where(a => a.ParentSpanId == span.SpanId).OrderBy(SpanCollector.StartedAt))
        {
            Print(child, depth + 1);
        }
    }

    Print(trace.Single(a => a.OperationName == "strata.run"), 0);
}

// The tags worth printing; the ids and timings are left out.
static string Tags(Activity span)
{
    string[] keys = ["strata.outcome", "strata.metadata.tenant", "strata.run_id", "strata.retry_count", "error.type", "order.risk"];
    var tags = keys
        .Where(key => key != "strata.run_id" || span.OperationName == "strata.run") // every engine span carries it
        .Select(key => (key, value: span.GetTagItem(key)))
        .Where(t => t.value is not null)
        .Select(t => $"{t.key}={t.value}")
        .ToList();
    if (span.Status == ActivityStatusCode.Error)
    {
        tags.Add("status=Error");
    }

    tags.AddRange(span.Events.Select(e => $"event:{e.Name}"));
    return tags.Count == 0 ? "" : "  " + string.Join(" ", tags);
}

static string Show(IReadOnlyDictionary<string, string>? metadata) =>
    metadata is null || metadata.Count == 0 ? "(none)" : string.Join(", ", metadata.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}"));

static string Outcome(GraphExecutionResult<Order> result) => result.GetType().Name.ToLowerInvariant();

static bool IsUuidV7(string? id) => Guid.TryParse(id, out var guid) && guid.Version == 7;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Order(int Amount, IReadOnlyList<string> Log)
{
    public static Order New(int amount) => new(amount, []);

    public Order Then(string step) => this with { Log = [.. Log, step] };
}
