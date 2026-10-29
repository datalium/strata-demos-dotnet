// Thread leases with fencing (design/32-Thread-Leases.md, SG-12): two workers, one thread.
//
// An invoice graph: Draft (an interrupt boundary, so the run freezes for approval after it) -> Send. Two
// workers, worker-a and worker-b, get the same invoice, as happens when a queue redelivers a message
// whose first consumer went quiet. Each runs with RunOptions.Lease = new LeaseOptions(owner, ttl):
//
//   1. While worker-a holds the thread, worker-b is refused (Failed "thread ... is held by 'worker-a'
//      until ...") and runs nothing. worker-a renews every ttl / 3, so a slow node keeps its thread.
//   2. Without leases, the problem they solve: worker-a stalls, worker-b drafts and freezes the invoice,
//      then worker-a wakes up and saves its stale draft on top. The thread now waits on the wrong draft.
//   3. With leases, the same stall: worker-a's lease runs out, worker-b takes the thread (token 2), and
//      worker-a's save under token 1 is refused. worker-a ends Cancelled(LeaseLost) and writes nothing,
//      not even a `cancelled` checkpoint.
//   4. The usual way a worker finds out: its next renewal is refused, its run is cancelled at once, and
//      the node's CancellationToken fires, so it stops working on a thread that is no longer its.
//
// Time is fake. The store has its own clock, the one lease expiry is judged by (Postgres uses its
// server clock for this). Each worker's run has its own clock, which drives its renewals. A stalled
// worker is one whose clock the demo doesn't move: a GC pause or a frozen VM fires no timers.
//
//   dotnet run --project demos/21-thread-leases/21-thread-leases.csproj

using System.Globalization;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Microsoft.Extensions.Time.Testing;

var ttl = TimeSpan.FromSeconds(30);
var noon = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

await HeldAsync();
await WithoutLeasesAsync();
await FencedSaveAsync();
await LostOnRenewalAsync();

Console.WriteLine();
Console.WriteLine("=== Done: one thread, one owner at a time, and a stale owner writes nothing. ===");
return 0;

// --- 1. A held thread refuses a second worker. ---

async Task HeldAsync()
{
    Console.WriteLine("=== Scenario 1: worker-a holds the thread, worker-b is refused ===");
    const string thread = "invoice-1";

    // One clock for the store and worker-a's run, so worker-a's renewals move its lease's expiry.
    var clock = new FakeTimeProvider(noon);
    var store = new InMemoryCheckpointStore(clock);
    var a = new Worker("worker-a");
    var b = new Worker("worker-b");
    var gate = new Gate();
    a.Stall = gate.HoldAsync;

    var aEvents = new RunEventChannel();
    var aRun = a.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-a", clock, aEvents));
    await gate.Started;

    var held = await store.GetLeaseAsync(thread);
    Console.WriteLine($"  worker-a is in Draft. Lease: '{held?.Owner}', token {held?.Token}, until {Text(held?.ExpiresAt)}.");
    var bEvents = new RunEventChannel();
    var refused = await b.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-b", new FakeTimeProvider(noon), bEvents));
    var refusal = (refused as GraphExecutionResult<Invoice>.Failed)?.Message;
    Console.WriteLine($"  worker-b -> Failed: {refusal}");
    Check(refusal == $"thread '{thread}' is held by 'worker-a' until 2026-10-24T12:00:30Z", "expected worker-b to be refused");
    Check(b.Drafts == 0 && await LogLengthAsync(store, thread) == 0, "expected the refused run to run and write nothing");
    var bSeen = await EventsAsync(bEvents);
    Console.WriteLine($"  worker-b's events: {Describe(bSeen)}. It drafted nothing and wrote nothing.");
    Check(!bSeen.OfType<LeaseAcquired>().Any(), "expected no LeaseAcquired for a refused run");

    // Draft takes longer than the ttl. Every third of a ttl worker-a renews, so it never lapses.
    for (var third = 1; third <= 4; third++)
    {
        var before = (await store.GetLeaseAsync(thread))!.ExpiresAt;
        clock.Advance(ttl / 3);
        await UntilAsync(async () => (await store.GetLeaseAsync(thread))?.ExpiresAt > before, $"renewal {third}");
    }

    var renewed = await store.GetLeaseAsync(thread);
    Console.WriteLine(
        $"  40 s later, still in Draft: worker-a renewed every 10 s. Lease: token {renewed?.Token}, until {Text(renewed?.ExpiresAt)}.");
    var again = await b.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-b", new FakeTimeProvider(noon)));
    Console.WriteLine($"  worker-b again -> Failed: {(again as GraphExecutionResult<Invoice>.Failed)?.Message}");
    Check(
        renewed is { Owner: "worker-a", Token: 1 } && again is GraphExecutionResult<Invoice>.Failed,
        "expected the renewed lease to keep worker-b out past the first ttl");

    gate.Release();
    var aResult = await aRun;
    Check(aResult is GraphExecutionResult<Invoice>.Interrupted, $"expected worker-a to freeze at Draft, got {aResult}");
    Console.WriteLine($"  worker-a froze at Draft for approval. Latest checkpoint: {Latest(store, thread)}.");
    var aSeen = await EventsAsync(aEvents);
    Console.WriteLine($"  worker-a's events: {Describe(aSeen)}.");
    Check(aSeen.OfType<LeaseAcquired>().Single() is { Owner: "worker-a", Token: 1 }, "expected worker-a to acquire token 1");
    Check(await store.GetLeaseAsync(thread) is null, "expected worker-a to release its lease when its run returned");
    Console.WriteLine("  Lease after the run: none. A lease lasts as long as one run, not as long as the thread.");
}

// --- 2. Without leases, the stale worker wins. ---

async Task WithoutLeasesAsync()
{
    Console.WriteLine();
    Console.WriteLine("=== Scenario 2: no leases -- the stale worker overwrites the new one ===");
    const string thread = "invoice-2";
    var store = new InMemoryCheckpointStore();
    var a = new Worker("worker-a");
    var b = new Worker("worker-b");
    var gate = new Gate();
    a.Stall = gate.HoldAsync;

    var aRun = a.Graph.RunAsync(thread, Invoice.New(thread), store);
    await gate.Started;
    Console.WriteLine("  worker-a stalls in Draft. The queue gives the invoice to worker-b.");
    var bResult = await b.Graph.RunAsync(thread, Invoice.New(thread), store);
    Check(bResult is GraphExecutionResult<Invoice>.Interrupted, "expected worker-b to freeze at Draft");
    Console.WriteLine($"  worker-b drafts and freezes. Latest checkpoint: {Latest(store, thread)}.");
    gate.Release();
    var aResult = await aRun;
    Check(aResult is GraphExecutionResult<Invoice>.Interrupted, "expected worker-a to freeze too");
    Console.WriteLine($"  worker-a wakes up and freezes too. Latest checkpoint: {Latest(store, thread)}.");
    Check(
        await LogLengthAsync(store, thread) == 2 && DraftedBy(store, thread) == "worker-a",
        "expected worker-a's stale draft to be the thread's latest checkpoint");
    Console.WriteLine("  Two checkpoints, and the latest is worker-a's: an approval now sends the stale draft.");
}

// --- 3. With leases, the stale worker's save is fenced. ---

async Task FencedSaveAsync()
{
    Console.WriteLine();
    Console.WriteLine("=== Scenario 3: leases -- worker-a's lease is taken over, and its save is refused ===");
    const string thread = "invoice-3";
    var storeClock = new FakeTimeProvider(noon);
    var store = new InMemoryCheckpointStore(storeClock);
    var a = new Worker("worker-a");
    var b = new Worker("worker-b");
    var gate = new Gate();
    a.Stall = gate.HoldAsync;

    // worker-a's own clock never moves: it is stalled, so it never renews and never notices.
    var aEvents = new RunEventChannel();
    var aRun = a.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-a", new FakeTimeProvider(noon), aEvents));
    await gate.Started;
    Console.WriteLine("  worker-a takes the lease (token 1, until 12:00:30) and stalls in Draft.");

    storeClock.Advance(ttl + TimeSpan.FromSeconds(1));
    var bEvents = new RunEventChannel();
    var bResult = await b.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-b", new FakeTimeProvider(noon), bEvents));
    Check(bResult is GraphExecutionResult<Invoice>.Interrupted, $"expected worker-b to freeze at Draft, got {bResult}");
    Console.WriteLine($"  At 12:00:31 the lease has expired. worker-b takes the thread and freezes it. Latest checkpoint: {Latest(store, thread)}.");
    var bSeen = await EventsAsync(bEvents);
    Console.WriteLine($"  worker-b's events: {Describe(bSeen)}.");
    Check(bSeen.OfType<LeaseAcquired>().Single() is { Owner: "worker-b", Token: 2 }, "expected worker-b to take the thread's second lease");

    Check(await store.GetLeaseAsync(thread) is null, "expected worker-b to have released the thread");

    gate.Release();
    var aResult = await aRun;
    var lost = aResult as GraphExecutionResult<Invoice>.Cancelled;
    Console.WriteLine($"  worker-a wakes up and finishes Draft -> {aResult.GetType().Name} ({lost?.Reason}) at '{lost?.Node}'.");
    Check(lost is { Reason: CancelReason.LeaseLost, Node: "Draft" }, $"expected worker-a's run to end LeaseLost, got {aResult}");
    var aSeen = await EventsAsync(aEvents);
    Console.WriteLine($"  worker-a's events: {Describe(aSeen)}.");
    CheckLost(aSeen);
    Check(
        await LogLengthAsync(store, thread) == 1 && DraftedBy(store, thread) == "worker-b",
        "expected worker-b's checkpoint to stay the only one");
    Console.WriteLine($"  The log still holds one checkpoint, worker-b's: {Latest(store, thread)}.");
    Console.WriteLine("  worker-b had already released its lease, so nobody held the thread: the save was refused");
    Console.WriteLine("  because token 1 is no longer the thread's latest. Expiry only lets someone else in; the token decides.");
}

// --- 4. A refused renewal cancels the run, and the node's token fires. ---

async Task LostOnRenewalAsync()
{
    Console.WriteLine();
    Console.WriteLine("=== Scenario 4: leases -- worker-a finds out at its next renewal ===");
    const string thread = "invoice-4";
    var storeClock = new FakeTimeProvider(noon);
    var aClock = new FakeTimeProvider(noon);
    var store = new InMemoryCheckpointStore(storeClock);
    var a = new Worker("worker-a");
    var b = new Worker("worker-b");
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tokenFired = 0;
    a.Stall = async ct =>
    {
        started.SetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Exchange(ref tokenFired, 1);
            throw;
        }
    };

    var aEvents = new RunEventChannel();
    var aRun = a.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-a", aClock, aEvents));
    await started.Task;
    storeClock.Advance(ttl + TimeSpan.FromSeconds(1));
    var bResult = await b.Graph.RunAsync(thread, Invoice.New(thread), store, Leased("worker-b", new FakeTimeProvider(noon)));
    Check(bResult is GraphExecutionResult<Invoice>.Interrupted, $"expected worker-b to freeze at Draft, got {bResult}");
    Console.WriteLine($"  worker-a's lease expired while it waited in Draft; worker-b took the thread. Latest checkpoint: {Latest(store, thread)}.");

    // worker-a's clock reaches its next renewal (a third of the ttl), which the store refuses.
    aClock.Advance(ttl / 3);
    var aResult = await aRun;
    var lost = aResult as GraphExecutionResult<Invoice>.Cancelled;
    Console.WriteLine($"  worker-a's renewal at +10 s is refused -> {aResult.GetType().Name} ({lost?.Reason}) at '{lost?.Node}', Draft unfinished.");
    Check(lost is { Reason: CancelReason.LeaseLost, Node: "Draft" }, $"expected worker-a's run to end LeaseLost, got {aResult}");

    // The engine returns without waiting for a handler it gave up on, so the token is seen a moment later.
    await UntilAsync(() => Task.FromResult(Volatile.Read(ref tokenFired) == 1), "Draft's token to fire");
    Console.WriteLine("  Draft's CancellationToken fired, so worker-a stopped working on a thread that isn't its.");
    var aSeen = await EventsAsync(aEvents);
    Console.WriteLine($"  worker-a's events: {Describe(aSeen)}.");
    CheckLost(aSeen);
    Check(
        await LogLengthAsync(store, thread) == 1 && DraftedBy(store, thread) == "worker-b",
        "expected worker-a to write nothing");
    Console.WriteLine($"  Still one checkpoint, worker-b's: {Latest(store, thread)}.");
}

RunOptions Leased(string owner, TimeProvider clock, IRunObserver? observer = null) =>
    new() { Lease = new LeaseOptions(owner, ttl), TimeProvider = clock, Observer = observer };

static async Task<int> LogLengthAsync(InMemoryCheckpointStore store, string thread) =>
    (await store.GetLogAsync(thread)).Entries.Count;

static string? DraftedBy(InMemoryCheckpointStore store, string thread) =>
    store.Load(thread)?.FrozenState?["drafted_by"]?.GetValue<string>();

static string Latest(InMemoryCheckpointStore store, string thread)
{
    var latest = store.Load(thread);
    return $"[{latest?.Status.ToString().ToLowerInvariant()} at {latest?.SuspendedNode}, drafted by {DraftedBy(store, thread)}]";
}

// The channel completes after RunCompleted, so this returns once the run's last event is in.
static async Task<List<RunEvent>> EventsAsync(RunEventChannel channel)
{
    var events = new List<RunEvent>();
    await foreach (var e in channel.Reader.ReadAllAsync())
    {
        events.Add(e);
    }

    return events;
}

static string Describe(List<RunEvent> events) => string.Join(", ", events.Select(e => e switch
{
    LeaseAcquired l => $"LeaseAcquired('{l.Owner}', token {l.Token}, until {Text(l.ExpiresAt)})",
    LeaseLost l => $"LeaseLost('{l.Owner}', token {l.Token})",
    NodeCompleted n => $"NodeCompleted({n.Result})",
    RunCompleted r when r.CancelReason is { } reason => $"RunCompleted({r.Outcome}, {reason})",
    RunCompleted r => $"RunCompleted({r.Outcome})",
    _ => e.GetType().Name,
}));

static async Task UntilAsync(Func<Task<bool>> condition, string what)
{
    // A real-time limit only so that a broken build fails instead of hanging; nothing is asserted on it.
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!await condition())
    {
        Check(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
        await Task.Delay(5);
    }
}

static string Text(DateTimeOffset? time) =>
    time?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "-";

// A lost lease: LeaseLost for worker-a's token, a run that ends Cancelled(LeaseLost), and no save at all.
static void CheckLost(List<RunEvent> events)
{
    Check(events.OfType<LeaseLost>().Single() is { Owner: "worker-a", Token: 1 }, "expected LeaseLost for worker-a's token 1");
    Check(
        events[^1] is RunCompleted { Outcome: RunOutcome.Cancelled, CancelReason: CancelReason.LeaseLost },
        "expected the run to complete Cancelled(LeaseLost)");
    Check(!events.OfType<CheckpointSaved>().Any(), "expected no CheckpointSaved from the stale worker");
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Invoice(
    [property: JsonPropertyName("invoice_id")] string InvoiceId,
    [property: JsonPropertyName("drafted_by")] string? DraftedBy,
    [property: JsonPropertyName("sent")] bool Sent)
{
    public static Invoice New(string id) => new(id, DraftedBy: null, Sent: false);
}

/// <summary>One worker process: its own compiled graph, which stamps the draft with the worker's name.</summary>
sealed class Worker
{
    private int _drafts;

    public Worker(string name)
    {
        Graph = new StateGraph<Invoice>()
            .AddNode("Draft", async (invoice, _, ct) =>
            {
                Interlocked.Increment(ref _drafts);
                if (Stall is { } stall)
                {
                    await stall(ct);
                }

                return invoice with { DraftedBy = name };
            })
            .AddInterruptBoundary("Draft")
            .AddNode("Send", invoice => invoice with { Sent = true })
            .AddEdge("Draft", "Send")
            .Compile();
    }

    public CompiledGraph<Invoice> Graph { get; }

    /// <summary>Runs inside Draft: how a scenario makes a worker slow.</summary>
    public Func<CancellationToken, Task>? Stall { get; set; }

    public int Drafts => Volatile.Read(ref _drafts);
}

/// <summary>A stall that ignores its token, like a paused process: it ends when the demo says so.</summary>
sealed class Gate
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _started.Task;

    public async Task HoldAsync(CancellationToken _)
    {
        _started.SetResult();
        await _release.Task;
    }

    public void Release() => _release.SetResult();
}
