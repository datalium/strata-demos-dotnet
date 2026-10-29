// Parallel fan-out / fan-in (design/14-Parallel-Execution.md).
//
// A trip planner -- ParseRequest -> GatherInfo -> BuildItinerary -- where GatherInfo is a *parallel
// group* (.AddParallelGroup(...)): it asks three simulated services at once (flights, hotels,
// weather) and merges what they return with a reducer. To the rest of the graph the group is one
// ordinary node, with ordinary edges in and out.
//
// Every service takes a different amount of time (flights 300 ms, hotels 200 ms, weather 100 ms),
// so the demo can watch four things:
//
//   1. Real concurrency  -- the three calls overlap (peak in flight: 3), so the group takes about
//                           as long as its slowest branch, not the sum.
//   2. A deterministic merge -- the services *finish* in the order weather, hotels, flights, but the
//                           reducer receives their results in the order the branches were
//                           *declared*, so the merged log is the same on every run.
//   3. Group-level failure -- the first branch to fail fails the whole group at once and cancels
//                           the siblings still running. The group's RecoveryPolicy then retries
//                           *every* branch (a transient blip), or routes to a fallback node on the
//                           pre-group state (a fatal error).
//   4. Streaming branches -- the same group under RunStreaming, each branch pushing tokens that
//                           arrive attributed to "GatherInfo/<branch>".
//
// The branches are async (Func<TState, NodeContext, CancellationToken, ValueTask<TState>>), so a
// cancelled sibling really stops: its Task.Delay throws. A sync branch can't see the cancellation;
// it would run on and have its result discarded. The engine doesn't wait for a branch it has given
// up on (design/26): the run can return a moment before the cancelled call notices its token, so
// the demo waits for that call to stop before counting it.
//
// No live service and no LLM -- the services are deterministic stand-ins (a delay and a canned
// answer) so the demo runs offline. Timings are measured, so the millisecond figures printed vary
// a little from run to run; the ordering and the counts do not.

using System.Diagnostics;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Streaming;

var store = new InMemoryCheckpointStore();

// --- Scenario 1: everything healthy. Concurrency + deterministic merge. ---

Console.WriteLine("=== Scenario 1: all three services healthy ===");
var tracker = new Tracker();
var services = NewServices(tracker, Fault.None, Fault.None, Fault.None);
var graph = BuildTripGraph(services, policy: null);
var wall = Stopwatch.StartNew();
var result = await graph.RunAsync("trip-1", TripPlan.New("  Lisbon "), store);
wall.Stop();
var completed = ExpectCompleted(result);
var finished = tracker.FinishedOrder();
Console.WriteLine($"  Completed in {wall.ElapsedMilliseconds} ms.");
Console.WriteLine("  Sequentially the three calls would take about 600 ms (300 + 200 + 100).");
Console.WriteLine($"  Peak branches in flight: {tracker.Peak} of 3 -- they really overlapped.");
Console.WriteLine($"  Completion order:  {string.Join(", ", finished)}");
Console.WriteLine("  Merged log (declaration order, identical on every run):");
foreach (var line in completed.State.Log)
{
    Console.WriteLine($"    - {line}");
}

Console.WriteLine($"  Summary: {completed.State.Summary}");
Check(tracker.Peak == 3, "expected all three branches to be in flight at once");
Check(finished.SequenceEqual(["weather", "hotels", "flights"]), "expected completion order weather, hotels, flights");
Check(
    completed.State.Log.Skip(1).SequenceEqual(
        ["flights: LH 401 for $612", "hotels: Hotel Aurora at $148/night", "weather: sunny, 24C"]),
    "expected the merged log in declaration order");

// --- Scenario 2: a transient blip in one branch retries the whole group. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: hotels times out once -- Retry(2) on the group ===");
tracker = new Tracker();
services = NewServices(tracker, Fault.None, new Fault(TimeoutFirst: 1), Fault.None);
graph = BuildTripGraph(services, RecoveryPolicy.Retry(2));
result = await graph.RunAsync("trip-2", TripPlan.New("Porto"), store);
await WaitForCancelled(services.Flights);
completed = ExpectCompleted(result);
Console.WriteLine(
    $"  Completed. Service calls -- flights: {services.Flights.Calls}, hotels: {services.Hotels.Calls}, weather: {services.Weather.Calls}.");
Console.WriteLine(
    $"  The failure cancelled flights mid-call ({services.Flights.Cancelled} cancelled), and the retry re-ran *all three*: " +
    $"{services.TotalCalls} calls in total, not the 4 a hotels-only retry would make. Branches must be safe to repeat.");
Console.WriteLine($"  Summary: {completed.State.Summary}");
Check(services.TotalCalls == 6, "expected the retry to re-run every branch (6 calls)");
Check(services.Flights.Cancelled == 1, "expected the hotels failure to cancel the flights call still in flight");
Check(completed.State.Log.Count == 4, "expected the failed attempt's log lines to be discarded");

// --- Scenario 3: a fatal branch fails the group; fall back. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: hotels has no rooms -- Retry(3), fallback 'UseCachedPlan' ===");
tracker = new Tracker();
services = NewServices(tracker, Fault.None, new Fault(RejectReason: "no rooms left on these dates"), Fault.None);
graph = BuildTripGraph(services, RecoveryPolicy.Retry(3).WithFallback("UseCachedPlan"));
result = await graph.RunAsync("trip-3", TripPlan.New("Madeira"), store);
await WaitForCancelled(services.Flights);
completed = ExpectCompleted(result);
Console.WriteLine(
    $"  Fell back after {services.TotalCalls} service call(s) in total -- a fatal error skips all 3 retries.");
Console.WriteLine(
    $"  Weather had already answered; flights was cancelled mid-call ({services.Flights.Cancelled} cancelled). " +
    "What finished is discarded with the failed group:");
Console.WriteLine(
    $"    Flight: {completed.State.Flight ?? "null"}, Hotel: {completed.State.Hotel ?? "null"}, Forecast: {completed.State.Forecast ?? "null"}");
Console.WriteLine($"  Log: [{string.Join(", ", completed.State.Log.Select(line => $"\"{line}\""))}]");
Console.WriteLine($"  Summary: {completed.State.Summary}");
Check(services.TotalCalls == 3, "expected a fatal error to skip retries (3 calls)");
Check(services.Flights.Cancelled == 1 && services.Weather.Answered == 1, "expected weather answered and flights cancelled");
Check(completed.State is { Flight: null, Forecast: null }, "expected sibling results to be discarded");
Check(completed.State.Log.Count == 1, "expected the fallback to run on the pre-group state");

// --- Scenario 4: the same fatal error with no fallback names the branch. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: same failure, no fallback -- the error names the branch ===");
tracker = new Tracker();
services = NewServices(tracker, Fault.None, new Fault(RejectReason: "no rooms left on these dates"), Fault.None);
graph = BuildTripGraph(services, RecoveryPolicy.Retry(3));
result = await graph.RunAsync("trip-4", TripPlan.New("Madeira"), store);
await WaitForCancelled(services.Flights);
if (result is not GraphExecutionResult<TripPlan>.NodeFailed failed)
{
    throw new InvalidOperationException($"expected NodeFailed, got: {result}");
}

Console.WriteLine($"  Failed: group '{failed.Node}' gave up after {failed.Attempts} attempt(s) -- {failed.Message}");
Console.WriteLine("  The cancelled flights call is not named: it didn't fail, it was stopped.");
Check(
    failed.Message == "branch 'hotels': no rooms left on these dates",
    "expected the failure message to name only the failing branch");

// --- Scenario 5: the same group, streaming. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 5: streaming branches under RunStreaming ===");
tracker = new Tracker();
services = NewServices(tracker, Fault.None, Fault.None, Fault.None);
graph = BuildTripGraph(services, policy: null, streaming: true);
var (reader, completion) = graph.RunStreaming("trip-5", TripPlan.New("Faro"), store);
var chunks = new List<StreamChunk>();
await foreach (var chunk in reader.ReadAllAsync())
{
    chunks.Add(chunk);
}

completed = ExpectCompleted(await completion);
var tokensBySource = chunks
    .Where(c => c.Type == StreamChunkType.Token)
    .GroupBy(c => c.NodeName)
    .OrderBy(g => g.Key, StringComparer.Ordinal)
    .ToList();
Console.WriteLine("  Tokens, grouped by the node name they arrived with (each branch's own stay in order):");
foreach (var source in tokensBySource)
{
    Console.WriteLine($"    {source.Key,-18} {string.Join(" | ", source.Select(c => c.Payload.ToString()))}");
}

var lifecycle = chunks.Where(c => c.Type == StreamChunkType.NodeStart).Select(c => c.NodeName).ToList();
Console.WriteLine($"  NodeStart chunks: {string.Join(", ", lifecycle)} -- branches are not nodes and get none.");
Console.WriteLine($"  Summary: {completed.State.Summary}");
Check(
    tokensBySource.Select(g => g.Key).SequenceEqual(["GatherInfo/flights", "GatherInfo/hotels", "GatherInfo/weather"]),
    "expected tokens attributed to GatherInfo/<branch>");
Check(tokensBySource.All(g => g.Count() == 2 && g.First().Payload.ToString() == "asking..."), "expected each branch's tokens in order");
Check(lifecycle.SequenceEqual(["ParseRequest", "GatherInfo", "BuildItinerary"]), "expected one lifecycle pair per node, none per branch");

// --- Compile-time guard: two branches with the same name. ---

Console.WriteLine();
Console.WriteLine("=== Step 6: a malformed group is rejected at .Compile(), not at runtime ===");
try
{
    var duplicate = new ParallelBranches<TripPlan>()
        .Branch("flights", plan => plan)
        .Branch("flights", plan => plan);
    new StateGraph<TripPlan>()
        .AddParallelGroup("GatherInfo", duplicate, Merge)
        .Compile();
    throw new InvalidOperationException("expected the duplicate branch name to be rejected");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("more than one branch"))
{
    Console.WriteLine($"  Rejected: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine(
    "=== Done: three services ran at once, merged in a fixed order, a failing branch was handled at the group level, and branches streamed. ===");

// Merges the three branch results back into one state.
//
// Each branch fills in its own field, and adds a line to Log after whatever entry.Log already held;
// the reducer keeps just that new suffix from each output. `outputs` is in *declaration* order
// (flights, hotels, weather) however the branches finished, which is what makes the merged log
// identical on every run.
static TripPlan Merge(TripPlan entry, IReadOnlyList<TripPlan> outputs)
{
    var merged = entry;
    foreach (var output in outputs)
    {
        merged = merged with
        {
            Flight = merged.Flight ?? output.Flight,
            Hotel = merged.Hotel ?? output.Hotel,
            Forecast = merged.Forecast ?? output.Forecast,
            Log = [.. merged.Log, .. output.Log.Skip(entry.Log.Count)],
        };
    }

    return merged;
}

// ParseRequest -> GatherInfo (flights | hotels | weather) -> BuildItinerary. When `policy` names a
// fallback node, UseCachedPlan is added as that node -- it gets no ordinary edge, so the recovery
// policy is the only way in. With `streaming`, the branches are StreamingBranches that push a token
// before and after their call.
static CompiledGraph<TripPlan> BuildTripGraph(Services services, RecoveryPolicy? policy, bool streaming = false)
{
    // Each branch receives the plan as it was when the group started and returns its own copy with
    // one field filled in. They can't see each other.
    Func<TripPlan, NodeContext, CancellationToken, ValueTask<TripPlan>> Ask(
        Service service, Func<TripPlan, TripPlan> answer, string line) =>
        async (plan, context, ct) =>
        {
            if (context.Stream is { } stream)
            {
                await stream.EmitTokenAsync("asking...", ct);
            }

            await service.CallAsync(ct);
            if (context.Stream is { } done)
            {
                await done.EmitTokenAsync(line, ct);
            }

            return answer(plan) with { Log = [.. plan.Log, line] };
        };

    var flights = Ask(services.Flights, plan => plan with { Flight = "LH 401, $612" }, "flights: LH 401 for $612");
    var hotels = Ask(services.Hotels, plan => plan with { Hotel = "Hotel Aurora, $148/night" }, "hotels: Hotel Aurora at $148/night");
    var weather = Ask(services.Weather, plan => plan with { Forecast = "sunny, 24C" }, "weather: sunny, 24C");

    var branches = streaming
        ? new ParallelBranches<TripPlan>()
            .StreamingBranch("flights", flights)
            .StreamingBranch("hotels", hotels)
            .StreamingBranch("weather", weather)
        : new ParallelBranches<TripPlan>()
            .Branch("flights", flights)
            .Branch("hotels", hotels)
            .Branch("weather", weather);

    var graph = new StateGraph<TripPlan>()
        .AddNode("ParseRequest", plan => plan with
        {
            Destination = plan.Destination.Trim(),
            Log = [.. plan.Log, $"request: trip to {plan.Destination.Trim()}"],
        })
        .AddParallelGroup("GatherInfo", branches, Merge)
        .AddNode("BuildItinerary", plan => plan with
        {
            Summary = $"{plan.Destination}: {plan.Flight ?? "-"} | {plan.Hotel ?? "-"} | {plan.Forecast ?? "-"}",
        })
        .AddEdge("ParseRequest", "GatherInfo")
        .AddEdge("GatherInfo", "BuildItinerary");

    if (policy is not null)
    {
        if (policy.Fallback is not null)
        {
            graph.AddNode(policy.Fallback, plan => plan with
            {
                Summary = $"{plan.Destination}: cached plan (live lookup unavailable)",
            });
        }

        graph.SetRecoveryPolicy("GatherInfo", policy);
    }

    return graph.Compile();
}

static Services NewServices(Tracker tracker, Fault flights, Fault hotels, Fault weather) =>
    new(
        new Service("flights", 300, flights, tracker),
        new Service("hotels", 200, hotels, tracker),
        new Service("weather", 100, weather, tracker));

static GraphExecutionResult<TripPlan>.Completed ExpectCompleted(GraphExecutionResult<TripPlan> result) =>
    result as GraphExecutionResult<TripPlan>.Completed
    ?? throw new InvalidOperationException($"expected Completed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

// The engine stops awaiting a branch it has given up on, so a run can return before the
// cancelled call has noticed its token. Give it up to 2 s to stop; the checks then say whether it did.
static async Task WaitForCancelled(Service service)
{
    var waited = Stopwatch.StartNew();
    while (service.Cancelled == 0 && waited.ElapsedMilliseconds < 2000)
    {
        await Task.Delay(10);
    }
}

sealed record TripPlan(
    string Destination,
    string? Flight,
    string? Hotel,
    string? Forecast,
    IReadOnlyList<string> Log,
    string? Summary)
{
    public static TripPlan New(string destination) =>
        new(destination, Flight: null, Hotel: null, Forecast: null, Log: [], Summary: null);
}

/// <summary>
/// What a scenario measures across all three services: how many branches were running at once, and
/// the order they finished in.
/// </summary>
sealed class Tracker
{
    private readonly Stopwatch _started = Stopwatch.StartNew();
    private readonly List<string> _finished = [];
    private int _inFlight;
    private int _peak;

    public int Peak => Volatile.Read(ref _peak);

    public long Ms => _started.ElapsedMilliseconds;

    public IReadOnlyList<string> FinishedOrder()
    {
        lock (_finished)
        {
            return [.. _finished];
        }
    }

    public void Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        do
        {
            seen = Volatile.Read(ref _peak);
        }
        while (now > seen && Interlocked.CompareExchange(ref _peak, now, seen) != seen);
    }

    public void Exit(string service)
    {
        Interlocked.Decrement(ref _inFlight);
        lock (_finished)
        {
            _finished.Add(service);
        }
    }
}

/// <summary>How a simulated service misbehaves.</summary>
/// <param name="TimeoutFirst">Times out for the first this-many calls, then works.</param>
/// <param name="RejectReason">If set, always rejects the request -- retrying can't help.</param>
sealed record Fault(int TimeoutFirst = 0, string? RejectReason = null)
{
    public static Fault None { get; } = new();
}

sealed class Service(string name, int latencyMs, Fault fault, Tracker tracker)
{
    private int _calls;
    private int _cancelled;
    private int _answered;

    public int Calls => Volatile.Read(ref _calls);

    public int Cancelled => Volatile.Read(ref _cancelled);

    public int Answered => Volatile.Read(ref _answered);

    /// <summary>One request to the service: takes its latency, then answers or throws. Stops if cancelled.</summary>
    public async Task CallAsync(CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        tracker.Enter();
        Console.WriteLine($"    [{tracker.Ms,4} ms] {name} call {call}: started");

        try
        {
            await Task.Delay(latencyMs, ct);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            tracker.Exit(name);
            Console.WriteLine($"    [{tracker.Ms,4} ms] {name} call {call}: cancelled (the group gave up on it)");
            throw;
        }

        NodeException? error = null;
        if (fault.RejectReason is not null)
        {
            error = NodeException.Fatal(fault.RejectReason);
        }
        else if (call <= fault.TimeoutFirst)
        {
            error = NodeException.Transient($"{name} timed out");
        }

        tracker.Exit(name);
        var outcome = error is null
            ? "answered"
            : $"{(error.Kind == NodeFailureKind.Transient ? "transient" : "fatal")} error: {error.Message}";
        Console.WriteLine($"    [{tracker.Ms,4} ms] {name} call {call}: {outcome}");

        if (error is not null)
        {
            throw error;
        }

        Interlocked.Increment(ref _answered);
    }
}

sealed record Services(Service Flights, Service Hotels, Service Weather)
{
    public int TotalCalls => Flights.Calls + Hotels.Calls + Weather.Calls;
}
