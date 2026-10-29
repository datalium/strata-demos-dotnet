// Model routes: failover and hedging over ordered candidates (design/46-Model-Routes.md).
//
// A ModelRoute is an IChatClient over a list of candidate clients. It asks them in order and moves
// on after a transient fault, a refusal (finish reason content_filter) or an answer its Accept
// rejects. A fatal fault (a 4xx such as 401) stops it. With HedgeAfter, a candidate that is slow to
// answer gets the next one started beside it, and the first accepted answer wins. A shared
// ModelHealth skips a provider that is cooling down after a Retry-After or whose breaker is open.
// When nothing answers, the node fails transiently with the shortest wait as its retry hint.
//
// Every route here sits behind an ordinary model node (AddModelNode, which calls StrataChat), so
// each attempt is a model call of the run, and the route records one ModelRouted run event saying
// which candidate answered and what happened to the others. The demo reads those events.
//
// No live model: every candidate is a FakeModel, a scripted IChatClient written below that answers,
// fails with a status and a Retry-After, refuses, or hangs. Hedging uses a 50 ms delay on the real
// clock, but the outcomes don't depend on timing: a "slow" candidate hangs until it is cancelled,
// or waits for a signal from the candidate started beside it. The breaker and the retry run on a
// FakeTimeProvider, so nothing is slept through.

using System.Collections.Concurrent;
using Datalium.Strata.AI;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

// --- Scenario 1: failover on a transient fault, a refusal and a rejected answer. ---

Console.WriteLine("=== Scenario 1: failover past a 429, a refusal and a rejected answer ===");
var primary = new FakeModel("northwind", "nw-large", Turn.Fail(429, TimeSpan.FromSeconds(30)));
var careful = new FakeModel("contoso", "ct-guarded", Turn.Refuse("I can't help with production systems."));
var terse = new FakeModel("fabrikam", "fb-mini", Turn.Answer("Restart it."));
var local = new FakeModel("local", "llama-local", Turn.Answer("Roll back deploy 412, because the error rate rose with it."));
var triage = new ModelRoute(new ModelRouteOptions
{
    Name = "triage",
    Candidates =
    [
        new ModelCandidate("primary", primary),
        new ModelCandidate("careful", careful),
        new ModelCandidate("terse", terse),
        new ModelCandidate("local", local),
    ],
    // The application's rule for a usable answer: it must give a reason.
    Accept = response => response.Text.Contains("because", StringComparison.OrdinalIgnoreCase) ? null : "it gives no reason",
});

var (result, events) = await Run(AskGraph(triage), "failover", new Ticket("Why is checkout failing?"));
var answer = ExpectCompleted(result).Answer;
Console.WriteLine($"  Answer: {answer}");
Console.WriteLine("  Model calls in the run (one per attempt, each naming the route):");
foreach (var e in events.Where(e => e is ModelCallStarted or ModelCallCompleted))
{
    Console.WriteLine($"    {Describe(e)}");
}

var routed = PrintRouted(events);
PrintHealth(triage.Health);
Check(answer.StartsWith("Roll back deploy 412", StringComparison.Ordinal), "expected local's answer");
Check(Outcomes(routed) == "primary:Failed:429, careful:Refused, terse:Rejected:it gives no reason, local:Answered",
    "expected primary to fail, careful to refuse, terse to be rejected and local to answer");
Check(routed.Answered == "local" && routed.Model == "llama-local", "expected local (llama-local) to answer");
Check(events.OfType<ModelCallStarted>().Count(s => s.Route == "triage") == 4, "expected four model calls, each in the route");
Check(routed.Attempts.Select(a => a.CallId).SequenceEqual(events.OfType<ModelCallStarted>().Select(s => s.CallId)),
    "expected each attempt to name its model call");
// Only the transient fault counts against health: a refusal or a rejection says nothing about availability.
Check(Health(triage.Health, "northwind") is { State: ModelHealthState.CoolingDown, Failures: 1 }, "expected northwind to cool down after its Retry-After");
Check(Health(triage.Health, "contoso") is { State: ModelHealthState.Available, Failures: 0 }, "expected a refusal to leave contoso's health alone");
Check(Health(triage.Health, "fabrikam") is { State: ModelHealthState.Available, Failures: 0 }, "expected a rejection to leave fabrikam's health alone");

// --- Scenario 2: a fatal fault stops the route. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: a 401 is fatal -- the route stops, and Retry(3) doesn't retry it ===");
var badKey = new FakeModel("northwind", "nw-large", Turn.Fail(401, message: "401 Unauthorized: invalid API key"));
var backup = new FakeModel("local", "llama-local", Turn.Answer("never asked"));
var fatal = new ModelRoute(new ModelRouteOptions
{
    Name = "fatal",
    Candidates = [new ModelCandidate("primary", badKey), new ModelCandidate("backup", backup)],
});

(result, events) = await Run(AskGraph(fatal, RecoveryPolicy.Retry(3)), "fatal", new Ticket("Why is checkout failing?"));
var failed = ExpectNodeFailed(result);
Console.WriteLine($"  Node '{failed.Node}' failed after {failed.Attempts} attempt(s): {failed.Message}");
routed = PrintRouted(events);
Check(failed.Attempts == 1, "expected a fatal fault to skip the retries");
Check(failed.Message == "model call failed (401): 401 Unauthorized: invalid API key", "expected the candidate's own fault, unchanged");
Check(Outcomes(routed) == "primary:Failed:401, backup:NotTried", "expected backup to be NotTried");
Check(backup.Calls == 0, "expected backup never to be asked");

// --- Scenario 3: hedging. A slow candidate gets the next one started beside it. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3a: HedgeAfter 50 ms -- slow hangs, fast starts beside it and wins ===");
var slow = new FakeModel("northwind", "nw-large", Turn.Hang());
var fast = new FakeModel("local", "llama-local", Turn.Answer("Roll back deploy 412, because the error rate rose with it."));
var hedged = new ModelRoute(new ModelRouteOptions
{
    Name = "hedged",
    Candidates = [new ModelCandidate("slow", slow), new ModelCandidate("fast", fast)],
    HedgeAfter = TimeSpan.FromMilliseconds(50),
});

(result, events) = await Run(AskGraph(hedged), "hedge-a", new Ticket("Why is checkout failing?"));
Console.WriteLine($"  Answer: {ExpectCompleted(result).Answer}");
routed = PrintRouted(events);
Check(Outcomes(routed) == "slow:LostRace, fast:Answered", "expected fast to win and slow to lose the race");
Check(routed.Hedges == 1, "expected one hedge");
Check(slow.Calls == 1 && fast.Calls == 1, "expected both candidates to be asked once");

Console.WriteLine();
Console.WriteLine("=== Scenario 3b: the same route shape -- slow answers once the hedge has started, and still wins ===");
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
// slow answers only after the hedge has started; the hedge never answers. Whatever the timing,
// both are running when slow's answer arrives, so the first answer -- slow's -- wins.
slow = new FakeModel("northwind", "nw-large", Turn.Answer("Roll back deploy 412, because the error rate rose with it.") with { WaitFor = started.Task });
var hedge = new FakeModel("local", "llama-local", Turn.Hang() with { OnAsked = () => started.TrySetResult() });
hedged = new ModelRoute(new ModelRouteOptions
{
    Name = "hedged",
    Candidates = [new ModelCandidate("slow", slow), new ModelCandidate("hedge", hedge)],
    HedgeAfter = TimeSpan.FromMilliseconds(50),
});

(result, events) = await Run(AskGraph(hedged), "hedge-b", new Ticket("Why is checkout failing?"));
Console.WriteLine($"  Answer: {ExpectCompleted(result).Answer}");
routed = PrintRouted(events);
Check(Outcomes(routed) == "slow:Answered, hedge:LostRace", "expected slow to win after the hedge had started");
Check(routed.Hedges == 1 && routed.Model == "nw-large", "expected one hedge and nw-large's answer");

// --- Scenario 4: Eligible skips a candidate for one request. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: Eligible -- the free plan skips the frontier candidate ===");
var frontier = new FakeModel("northwind", "nw-large", Turn.Answer("Roll back deploy 412, because the error rate rose with it."));
var cheap = new FakeModel("local", "llama-local", Turn.Answer("Roll back deploy 412, because it is the newest change."));
var eligible = new ModelRoute(new ModelRouteOptions
{
    Name = "plans",
    Candidates =
    [
        new ModelCandidate("frontier", frontier)
        {
            // The application's rule, asked per request: here the run's metadata says the customer's plan.
            Eligible = (_, _, ctx) => ctx?.Metadata.GetValueOrDefault("plan") == "free" ? "the free plan doesn't use frontier models" : null,
        },
        new ModelCandidate("local", cheap),
    ],
});
var plans = AskGraph(eligible);

(result, events) = await Run(plans, "plan-paid", new Ticket("Why is checkout failing?"), new() { ["plan"] = "paid" });
ExpectCompleted(result);
Console.WriteLine("  plan=paid:");
var paid = PrintRouted(events);
(result, events) = await Run(plans, "plan-free", new Ticket("Why is checkout failing?"), new() { ["plan"] = "free" });
ExpectCompleted(result);
Console.WriteLine("  plan=free:");
var free = PrintRouted(events);
Check(Outcomes(paid) == "frontier:Answered, local:NotTried", "expected frontier to answer the paid request");
Check(Outcomes(free) == "frontier:Ineligible:the free plan doesn't use frontier models, local:Answered", "expected frontier to be skipped for the free request");
Check(frontier.Calls == 1 && cheap.Calls == 1, "expected the ineligible candidate not to be asked");

// --- Scenario 5: one ModelHealth shared by two routes; the breaker skips a failing provider. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 5: a shared ModelHealth with Breaker(2 failures, open 60 s) across two routes ===");
var clock = new FakeTimeProvider();
var shared = new ModelHealth(new ModelHealthOptions
{
    Breaker = new ModelBreaker(Failures: 2, OpenFor: TimeSpan.FromSeconds(60)),
    TimeProvider = clock,
});
// Two models of one provider: the health key is the provider's name, so an outage counts against both.
var large = new FakeModel("northwind", "nw-large", Turn.Fail(503), Turn.Answer("Roll back deploy 412, because the error rate rose with it."));
var small = new FakeModel("northwind", "nw-small", Turn.Fail(503), Turn.Answer("Deploy 412 broke checkout."));
var steady = new FakeModel("local", "llama-local",
    Turn.Answer("Roll back deploy 412, because it is the newest change."), Turn.Answer("Deploy 412 broke checkout."),
    Turn.Answer("Roll back deploy 412, because it is the newest change."), Turn.Answer("Deploy 412 broke checkout."));
var diagnose = new ModelRoute(new ModelRouteOptions
{
    Name = "diagnose",
    Candidates = [new ModelCandidate("large", large), new ModelCandidate("steady", steady)],
    Health = shared,
});
var summarise = new ModelRoute(new ModelRouteOptions
{
    Name = "summarise",
    Candidates = [new ModelCandidate("small", small), new ModelCandidate("steady", steady)],
    Health = shared,
});
var desk = new StateGraph<Ticket>()
    .AddModelNode<Ticket>("Diagnose", diagnose, (t, _) => [new(ChatRole.User, t.Question)], (t, r) => t with { Answer = r.Text })
    .AddModelNode<Ticket>("Summarise", summarise, (t, _) => [new(ChatRole.User, t.Answer)], (t, r) => t with { Summary = r.Text })
    .AddEdge("Diagnose", "Summarise")
    .Compile();

Console.WriteLine("  Run 1: northwind fails in each route (2 failures in a row: the breaker opens)");
(result, events) = await Run(desk, "desk-1", new Ticket("Why is checkout failing?"));
ExpectCompleted(result);
var run1 = PrintAllRouted(events);
PrintHealth(shared);
Check(run1.Select(Outcomes).SequenceEqual(["large:Failed:503, steady:Answered", "small:Failed:503, steady:Answered"]), "expected northwind to fail in both routes");
Check(Health(shared, "northwind") is { State: ModelHealthState.Open, Failures: 2 }, "expected northwind's breaker to be open");

Console.WriteLine("  Run 2: northwind's breaker is open, so neither route asks it");
(result, events) = await Run(desk, "desk-2", new Ticket("Why is checkout failing?"));
ExpectCompleted(result);
var run2 = PrintAllRouted(events);
Check(run2.Select(Outcomes).SequenceEqual(["large:BreakerOpen:60 s, steady:Answered", "small:BreakerOpen:60 s, steady:Answered"]), "expected northwind to be skipped");
Check(large.Calls == 1 && small.Calls == 1, "expected northwind not to be asked while its breaker is open");

clock.Advance(TimeSpan.FromSeconds(60));
Console.WriteLine("  (fake clock +60 s)");
PrintHealth(shared);
Check(Health(shared, "northwind") is { State: ModelHealthState.HalfOpen }, "expected northwind to be half-open");

Console.WriteLine("  Run 3: one test call goes through, succeeds and closes the breaker");
(result, events) = await Run(desk, "desk-3", new Ticket("Why is checkout failing?"));
ExpectCompleted(result);
var run3 = PrintAllRouted(events);
PrintHealth(shared);
Check(run3.Select(Outcomes).SequenceEqual(["large:Answered, steady:NotTried", "small:Answered, steady:NotTried"]), "expected northwind to answer again");
Check(Health(shared, "northwind") is { State: ModelHealthState.Available, Failures: 0 }, "expected the breaker to close");

// --- Scenario 6: nothing answers. The node fails transiently, and its RecoveryPolicy waits for the hint. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 6: nothing answers -- a transient failure whose hint is the shortest wait ===");
var clock6 = new FakeTimeProvider();
var a = new FakeModel("northwind", "nw-large", Turn.Fail(429, TimeSpan.FromSeconds(8)));
var b = new FakeModel("contoso", "ct-guarded", Turn.Fail(503, TimeSpan.FromSeconds(3)), Turn.Answer("Roll back deploy 412, because the error rate rose with it."));
var exhausted = new ModelRoute(new ModelRouteOptions
{
    Name = "exhausted",
    Candidates = [new ModelCandidate("a", a), new ModelCandidate("b", b)],
    Health = new ModelHealth(new ModelHealthOptions { TimeProvider = clock6 }),
});
var retrying = AskGraph(exhausted, RecoveryPolicy.Retry(2).WithBackoff(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30)));

var recorder = new Recorder();
var run = retrying.RunAsync("exhausted", new Ticket("Why is checkout failing?"), new InMemoryCheckpointStore(),
    new RunOptions { Observer = recorder, TimeProvider = clock6 });
// The clock stands still until the route has failed, so its hint is exact; then it moves until the retry runs.
await recorder.RetryScheduled.Task.WaitAsync(Limits.Patience);
for (var i = 0; i < 1000 && !run.IsCompleted; i++)
{
    clock6.Advance(TimeSpan.FromMilliseconds(500));
    await Task.Delay(5);
}

answer = ExpectCompleted(await run.WaitAsync(Limits.Patience)).Answer;
events = await recorder.Drain();
var retry = events.OfType<RetryScheduled>().Single();
Console.WriteLine($"  Attempt 1 failed: {retry.Error}");
Console.WriteLine($"  RetryScheduled delay: {retry.Delay.TotalSeconds} s (b's Retry-After, the shorter of 8 s and 3 s; not the 200 ms backoff)");
var attempts = PrintAllRouted(events);
Console.WriteLine($"  Answer: {answer}");
Check(retry.Error == "model call failed (route_exhausted): no candidate of route 'exhausted' answered: a failed (429), b failed (503)",
    "expected the route's message to name each candidate");
Check(retry.Delay == TimeSpan.FromSeconds(3), "expected the retry to wait for the shortest hint, 3 s");
Check(attempts.Count == 2, "expected two calls through the route");
Check(Outcomes(attempts[0]) == "a:Failed:429, b:Failed:503", "expected both candidates to fail on the first attempt");
Check(attempts[1].Attempts[0].Outcome == ModelRouteOutcome.CoolingDown && attempts[1].Attempts[1].Outcome == ModelRouteOutcome.Answered,
    "expected the retry to skip a (still cooling down) and get b's answer");
Check(a.Calls == 1 && b.Calls == 2, "expected a to be asked once and b twice");

Console.WriteLine();
Console.WriteLine("=== Done: the route failed over, stopped on a fatal fault, hedged, skipped by rule and by health, and handed its hint to the retry. ===");

static CompiledGraph<Ticket> AskGraph(IChatClient chat, RecoveryPolicy? policy = null)
{
    var graph = new StateGraph<Ticket>()
        .AddModelNode<Ticket>("Ask", chat, (ticket, _) => [new(ChatRole.User, ticket.Question)], (ticket, response) => ticket with { Answer = response.Text });
    if (policy is not null)
    {
        graph.SetRecoveryPolicy("Ask", policy);
    }

    return graph.Compile();
}

static async Task<(GraphExecutionResult<Ticket> Result, List<RunEvent> Events)> Run(
    CompiledGraph<Ticket> graph, string thread, Ticket ticket, Dictionary<string, string>? metadata = null)
{
    var recorder = new Recorder();
    var result = await graph.RunAsync(thread, ticket, new InMemoryCheckpointStore(), new RunOptions { Observer = recorder, Metadata = metadata ?? [] });
    return (result, await recorder.Drain());
}

static ModelRouted PrintRouted(List<RunEvent> events)
{
    var routed = PrintAllRouted(events);
    Check(routed.Count == 1, "expected one call through the route");
    return routed[0];
}

static List<ModelRouted> PrintAllRouted(List<RunEvent> events)
{
    var routed = events.OfType<ModelRouted>().ToList();
    foreach (var r in routed)
    {
        var who = r.Answered is null ? "nobody answered" : $"answered by {r.Answered} ({r.Model})";
        Console.WriteLine($"  ModelRouted {r.NodePath}#{r.Attempt} route '{r.Route}': {who}, hedges {r.Hedges}");
        foreach (var attempt in r.Attempts)
        {
            Console.WriteLine($"    {attempt.Candidate,-9} {attempt.Outcome,-12} {attempt.Reason}".TrimEnd());
        }
    }

    return routed;
}

static void PrintHealth(ModelHealth health)
{
    var entries = health.Snapshot().Select(e => $"{e.Key}={e.State}({e.Failures})");
    Console.WriteLine($"  Health: {string.Join(", ", entries)}");
}

static ModelHealthEntry? Health(ModelHealth health, string key) => health.Snapshot().SingleOrDefault(e => e.Key == key);

static string Outcomes(ModelRouted routed) =>
    string.Join(", ", routed.Attempts.Select(a => a.Reason is null ? $"{a.Candidate}:{a.Outcome}" : $"{a.Candidate}:{a.Outcome}:{a.Reason}"));

static string Describe(RunEvent e) => e switch
{
    ModelCallStarted s => $"ModelCallStarted   {s.Model,-12} route={s.Route}",
    ModelCallCompleted c => $"ModelCallCompleted {c.Model,-12} {c.Result}{(c.FinishReason is null ? "" : $" ({c.FinishReason})")}",
    _ => e.GetType().Name,
};

static Ticket ExpectCompleted(GraphExecutionResult<Ticket> result) =>
    result is GraphExecutionResult<Ticket>.Completed completed
        ? completed.State
        : throw new InvalidOperationException($"expected the run to complete, got: {result}");

static GraphExecutionResult<Ticket>.NodeFailed ExpectNodeFailed(GraphExecutionResult<Ticket> result) =>
    result as GraphExecutionResult<Ticket>.NodeFailed ?? throw new InvalidOperationException($"expected NodeFailed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Ticket(string Question, string Answer = "", string Summary = "");

/// <summary>One scripted turn of a <see cref="FakeModel"/>.</summary>
sealed record Turn
{
    public string Text { get; init; } = "";

    /// <summary>Answer with finish reason content_filter.</summary>
    public bool Refused { get; init; }

    public Exception? Failure { get; init; }

    /// <summary>Wait until the call is cancelled (a hedge's loser).</summary>
    public bool Hanging { get; init; }

    /// <summary>Wait for this before answering.</summary>
    public Task? WaitFor { get; init; }

    /// <summary>Called as soon as the turn is asked.</summary>
    public Action? OnAsked { get; init; }

    public static Turn Answer(string text) => new() { Text = text };

    public static Turn Refuse(string text) => new() { Text = text, Refused = true };

    public static Turn Hang() => new() { Hanging = true };

    /// <summary>
    /// A provider's HTTP fault, as RetryAfterHandler or an application's own client throws it:
    /// 408, 409, 429 and 5xx are transient, every other status fatal.
    /// </summary>
    public static Turn Fail(int status, TimeSpan? retryAfter = null, string? message = null) => new()
    {
        Failure = new ModelCallException(
            message ?? $"{status} from the provider",
            isTransient: ModelFaults.IsTransientStatus(status),
            statusCode: status,
            retryAfter: retryAfter),
    };
}

/// <summary>
/// A scripted stand-in for a provider's chat client: it plays its turns in order and names its
/// provider and model in its metadata (the route's default health key is the provider's name).
/// </summary>
sealed class FakeModel(string provider, string model, params Turn[] script) : IChatClient
{
    private readonly ConcurrentQueue<Turn> _script = new(script);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        if (!_script.TryDequeue(out var turn))
        {
            throw new InvalidOperationException($"{model} has no more scripted turns");
        }

        turn.OnAsked?.Invoke();
        if (turn.Hanging)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (turn.WaitFor is { } signal)
        {
            await signal.WaitAsync(cancellationToken);
        }

        await Task.Yield();
        if (turn.Failure is { } failure)
        {
            throw failure;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, turn.Text))
        {
            ModelId = options?.ModelId ?? model,
            FinishReason = turn.Refused ? ChatFinishReason.ContentFilter : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 20, OutputTokenCount = 8 },
        };
    }

    // The demo's runs don't stream, so StrataChat makes whole calls only.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the demo's runs don't stream");

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(provider, null, model) : null;

    public void Dispose()
    {
    }
}

/// <summary>Collects one run's events; completes when RunCompleted arrives, which may be after RunAsync returns.</summary>
sealed class Recorder : IRunObserver
{
    private readonly ConcurrentQueue<RunEvent> _events = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource RetryScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnEvent(RunEvent runEvent)
    {
        _events.Enqueue(runEvent);
        if (runEvent is RetryScheduled)
        {
            RetryScheduled.TrySetResult();
        }

        if (runEvent is RunCompleted)
        {
            _completed.TrySetResult();
        }
    }

    public async Task<List<RunEvent>> Drain()
    {
        await _completed.Task.WaitAsync(Limits.Patience);
        return [.. _events];
    }
}

static class Limits
{
    /// <summary>How long the demo waits for something that should happen at once before calling it a failure.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
}
