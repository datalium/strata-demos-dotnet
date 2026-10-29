// The run scheduler (design/40-Run-Scheduler.md): admitting runs to a fixed number of slots.
//
// A RunScheduler is given to a run through RunOptions.Scheduler. Before a run loads or writes
// anything it waits for a slot; it gives the slot back when RunAsync returns. Four scenarios, one
// rule each:
//
//   1. Strict priority. Higher RunOptions.Priority first; first come, first served within one.
//   2. A reserved slot. A slot kept for priority 5 and up: a low-priority run waits beside a free
//      slot, and an urgent run takes it at once.
//   3. Tenants. At most one run per tenant (the run's metadata "tenant"). A tenant at its limit
//      holds back only its own runs, a limit of 0 pauses a tenant, and SetTenantLimit changes a
//      limit while runs wait.
//   4. A bounded queue. When it is full, an equal-priority newcomer is refused and a higher-priority
//      one sheds the lowest-ranked waiting run. Both get GraphExecutionResult.NotAdmitted, and
//      neither leaves a checkpoint or a run event behind.
//
// Every run's one node waits on a TaskCompletionSource the demo releases, so which runs hold slots
// at any moment is decided by the demo, not by timing. The order of admission is read from the
// order the nodes started. The clock is a FakeTimeProvider: how long a run queued (RunStarted.QueuedFor,
// NotAdmitted.Waited) is an exact figure. No host, no LLM, no network.

using System.Collections.Concurrent;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Scheduling;
using Datalium.Strata.Storage;
using Microsoft.Extensions.Time.Testing;

// --- Scenario 1: strict priority, FIFO within a priority ---

Console.WriteLine("=== Scenario 1: one slot, five runs waiting at three priorities ===");
var lab = new Lab(new RunSchedulerOptions { Name = "priority", MaxConcurrent = 1 });
await lab.StartAndWaitAsync("blocker");
foreach (var (id, priority) in new[] { ("low-1", 0), ("high-1", 5), ("low-2", 0), ("high-2", 5), ("mid-1", 2) })
{
    await lab.QueueAsync(id, priority);
}

Console.WriteLine("  Arrived in this order: low-1 (p0), high-1 (p5), low-2 (p0), high-2 (p5), mid-1 (p2)");
Console.WriteLine($"  {lab.Describe()}");
Check(lab.Scheduler.Snapshot() is { Active: 1, Queued: 5 }, "expected one run active and five queued");

// Each minute of the fake clock, the run holding the slot finishes and the next one starts.
var holder = "blocker";
for (var minute = 1; minute <= 5; minute++)
{
    lab.Clock.Advance(TimeSpan.FromMinutes(1));
    holder = await lab.FinishAndNextAsync(holder);
}

await lab.FinishAsync(holder);
var order = lab.StartOrder.Skip(1).ToList();
Console.WriteLine($"  Started in this order: {string.Join(", ", order)}");
foreach (var id in order)
{
    Console.WriteLine($"    {id,-6} RunStarted.QueuedFor = {await lab.QueuedForAsync(id)}");
}

Check(order.SequenceEqual(["high-1", "high-2", "mid-1", "low-1", "low-2"]), "expected priority order, FIFO within a priority");
Check(await lab.QueuedForAsync("blocker") == TimeSpan.Zero, "expected the first run admitted on arrival");
Check(await lab.QueuedForAsync("low-2") == TimeSpan.FromMinutes(5), "expected low-2 to have queued five minutes");

// --- Scenario 2: a reserved slot ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: three slots, one kept for priority 5 and up ===");
lab = new Lab(new RunSchedulerOptions
{
    Name = "reserved",
    MaxConcurrent = 3,
    Reservations = [new SlotReservation(MinPriority: 5, Slots: 1)],
});
await lab.StartAndWaitAsync("batch-1");
await lab.StartAndWaitAsync("batch-2");
await lab.QueueAsync("batch-3");
Console.WriteLine($"  batch-1..3 (p0): {lab.Describe()}");
Console.WriteLine("  batch-3 waits beside a free slot: that slot is the reserve.");
Check(lab.Scheduler.Snapshot() is { Active: 2, Queued: 1 }, "expected batch-3 to wait while a slot is free");

await lab.StartAndWaitAsync("urgent", priority: 5);
Console.WriteLine($"  urgent (p5) arrives and starts at once: {lab.Describe()}");
Check(lab.Scheduler.Snapshot() is { Active: 3, Queued: 1 }, "expected urgent to take the reserved slot");

await lab.FinishAsync("urgent");
Console.WriteLine($"  urgent finishes; its slot goes back to the reserve: {lab.Describe()}");
Check(lab.Scheduler.Snapshot() is { Active: 2, Queued: 1 }, "expected batch-3 still to wait: a reservation lends nothing back");

var next = await lab.FinishAndNextAsync("batch-1");
Console.WriteLine($"  batch-1 finishes; {next} starts: {lab.Describe()}");
Check(next == "batch-3", "expected batch-3 to start once an unreserved slot was free");
await lab.FinishAsync("batch-2");
await lab.FinishAsync("batch-3");

// --- Scenario 3: tenant limits ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: four slots, one run per tenant, initech paused ===");
lab = new Lab(new RunSchedulerOptions
{
    Name = "tenants",
    MaxConcurrent = 4,
    TenantLimit = 1,
    TenantLimits = new Dictionary<string, int> { ["initech"] = 0 },
});
await lab.StartAndWaitAsync("acme-1", tenant: "acme");
await lab.QueueAsync("acme-2", tenant: "acme");
await lab.StartAndWaitAsync("globex-1", tenant: "globex");
await lab.QueueAsync("initech-1", tenant: "initech");
Console.WriteLine("  Arrived: acme-1, acme-2, globex-1, initech-1");
Console.WriteLine($"  {lab.Describe()}");
foreach (var (tenant, usage) in lab.Scheduler.Snapshot().Tenants.OrderBy(t => t.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"    {tenant,-8} active {usage.Active}, queued {usage.Queued}, limit {usage.Limit}");
}

var tenants = lab.Scheduler.Snapshot().Tenants;
Check(lab.StartOrder.SequenceEqual(["acme-1", "globex-1"]), "expected globex-1 to start past acme-2, which waits on its tenant");
Check(
    tenants["acme"] == new TenantUsage(1, 1, 1) && tenants["globex"] == new TenantUsage(1, 0, 1)
        && tenants["initech"] == new TenantUsage(0, 1, 0),
    "expected the per-tenant counts and limits");

lab.Scheduler.SetTenantLimit("initech", 1);
await lab.WaitStartedAsync("initech-1");
Console.WriteLine($"  SetTenantLimit(\"initech\", 1): initech-1 starts. {lab.Describe()}");
lab.Scheduler.SetTenantLimit("acme", 2);
await lab.WaitStartedAsync("acme-2");
Console.WriteLine($"  SetTenantLimit(\"acme\", 2): acme-2 starts. {lab.Describe()}");
Check(lab.Scheduler.Snapshot() is { Active: 4, Queued: 0 }, "expected every run active once the limits were raised");
foreach (var id in (string[])["acme-1", "acme-2", "globex-1", "initech-1"])
{
    await lab.FinishAsync(id);
}

// --- Scenario 4: a bounded queue that sheds ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: one slot, room for two to wait ===");
lab = new Lab(new RunSchedulerOptions { Name = "shedding", MaxConcurrent = 1, MaxQueued = 2 });
await lab.StartAndWaitAsync("blocker");
await lab.QueueAsync("report-a");
await lab.QueueAsync("report-b");
Console.WriteLine($"  blocker running, report-a and report-b (p0) waiting: {lab.Describe()}");

// An equal-priority newcomer outranks nobody: it is refused at once.
var reportC = await lab.Launch("report-c");
Console.WriteLine($"  report-c (p0) arrives: {Refusal(reportC)}");
Check(reportC is GraphExecutionResult<Job>.NotAdmitted { Reason: NotAdmittedReason.QueueFull, Waited.Ticks: 0 },
    "expected report-c refused at once with QueueFull");

// A higher-priority newcomer sheds the lowest-ranked waiting run: lowest priority, latest arrival.
lab.Clock.Advance(TimeSpan.FromSeconds(30));
var page = lab.Launch("page-oncall", priority: 5);
var reportB = await lab.ResultAsync("report-b");
Console.WriteLine($"  page-oncall (p5) arrives 30 s later: report-b {Refusal(reportB)}");
Console.WriteLine($"  {lab.Describe()}");
Check(reportB is GraphExecutionResult<Job>.NotAdmitted { Reason: NotAdmittedReason.Shed } shed && shed.Waited == TimeSpan.FromSeconds(30),
    "expected report-b shed after 30 s in the queue");
Check(lab.Scheduler.Snapshot() is { Active: 1, Queued: 2 }, "expected page-oncall and report-a waiting");

next = await lab.FinishAndNextAsync("blocker");
Check(next == "page-oncall", "expected page-oncall to start first");
next = await lab.FinishAndNextAsync("page-oncall");
Check(next == "report-a", "expected report-a to start next");
await lab.FinishAsync("report-a");
_ = await page;
Console.WriteLine($"  Started after blocker: {string.Join(", ", lab.StartOrder.Skip(1))}");

// What the refused runs left behind: nothing. The ones that ran left checkpoints and events.
await lab.EventsDeliveredAsync(["blocker", "page-oncall", "report-a"]);
var left = new Dictionary<string, (int Checkpoints, int Events)>();
foreach (var id in (string[])["report-a", "report-b", "report-c"])
{
    left[id] = (await lab.CheckpointCountAsync(id), lab.EventCount(id));
    Console.WriteLine($"    {id}: {left[id].Checkpoints} checkpoint(s), {left[id].Events} run event(s)");
}

Check(left["report-a"] is { Checkpoints: > 0, Events: > 0 }, "expected report-a to leave checkpoints and events");
Check(left["report-b"] == (0, 0) && left["report-c"] == (0, 0), "expected the refused runs to leave nothing behind");

Console.WriteLine();
Console.WriteLine("=== Done: priority, a reserve, tenant limits and shedding, each decided before a run touched the store. ===");

static string Refusal(GraphExecutionResult<Job> result) => result is GraphExecutionResult<Job>.NotAdmitted refused
    ? $"NotAdmitted({refused.Reason}) after {refused.Waited} -- \"{refused.Message}\""
    : $"unexpectedly {result}";

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Job(string Id, bool Done);

/// <summary>
/// One scheduler, one graph and one store for a scenario, plus a gate per run: the run's only node
/// records that it started and then waits until the demo opens its gate.
/// </summary>
sealed class Lab
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly CompiledGraph<Job> _graph;
    private readonly InMemoryCheckpointStore _store;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
    private readonly ConcurrentDictionary<string, Task<GraphExecutionResult<Job>>> _runs = new();
    private readonly ConcurrentQueue<string> _started = new();
    private readonly ConcurrentQueue<RunEvent> _events = new();
    private readonly Observer _observer;

    public Lab(RunSchedulerOptions options)
    {
        Scheduler = new RunScheduler(options);
        _store = new InMemoryCheckpointStore(Clock);
        _observer = new Observer(_events);

        // EveryNode: a run that gets a slot writes a checkpoint before its node and one when it
        // completes, so "nothing written" for a refused run is a real contrast.
        _graph = new StateGraph<Job>()
            .AddNode("Work", async (Job job, NodeContext _, CancellationToken _) =>
            {
                _started.Enqueue(job.Id);
                await _gates[job.Id].Task;
                return job with { Done = true };
            })
            .Compile(new CompilationOptions { Checkpointing = CheckpointPolicy.EveryNode });
    }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    public RunScheduler Scheduler { get; }

    public IReadOnlyList<string> StartOrder => [.. _started];

    /// <summary>Calls RunAsync. The run takes its place in the queue during this call.</summary>
    public Task<GraphExecutionResult<Job>> Launch(string id, int priority = 0, string? tenant = null)
    {
        _gates[id] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RunOptions
        {
            Scheduler = Scheduler,
            Priority = priority,
            TimeProvider = Clock,
            Observer = _observer,
        };
        if (tenant is not null)
        {
            options.Metadata["tenant"] = tenant;
        }

        var run = _graph.RunAsync(id, new Job(id, Done: false), _store, options);
        _runs[id] = run;
        return run;
    }

    /// <summary>Launches a run that should get a slot at once, and waits for its node to start.</summary>
    public async Task StartAndWaitAsync(string id, int priority = 0, string? tenant = null)
    {
        _ = Launch(id, priority, tenant);
        await WaitStartedAsync(id);
    }

    /// <summary>Launches a run that should wait, and checks that it does.</summary>
    public async Task QueueAsync(string id, int priority = 0, string? tenant = null)
    {
        // Waiting for the queue to grow before the next launch makes the arrival order the call order.
        var before = Scheduler.Snapshot().Queued;
        _ = Launch(id, priority, tenant);
        await Until(() => Scheduler.Snapshot().Queued == before + 1, $"{id} to join the queue");
        if (_started.Contains(id))
        {
            throw new InvalidOperationException($"expected {id} to wait for a slot");
        }
    }

    public Task WaitStartedAsync(string id) => Until(() => _started.Contains(id), $"{id} to start");

    /// <summary>Opens a running run's gate and waits for it to return Completed, slot given back.</summary>
    public async Task FinishAsync(string id)
    {
        _gates[id].TrySetResult();
        var result = await ResultAsync(id);
        if (result is not GraphExecutionResult<Job>.Completed)
        {
            throw new InvalidOperationException($"expected {id} to complete, got: {result}");
        }
    }

    /// <summary>Finishes <paramref name="id"/> and returns the run that started in its slot.</summary>
    public async Task<string> FinishAndNextAsync(string id)
    {
        var count = _started.Count;
        await FinishAsync(id);
        await Until(() => _started.Count > count, $"a run to start after {id}");
        return _started.ElementAt(count);
    }

    public Task<GraphExecutionResult<Job>> ResultAsync(string id) => _runs[id].WaitAsync(Patience);

    /// <summary>The run's RunStarted.QueuedFor. Events arrive off the run's path, so this waits for it.</summary>
    public async Task<TimeSpan?> QueuedForAsync(string id)
    {
        await Until(() => _events.OfType<RunStarted>().Any(e => e.ThreadId == id), $"{id}'s RunStarted");
        return _events.OfType<RunStarted>().Single(e => e.ThreadId == id).QueuedFor;
    }

    public Task EventsDeliveredAsync(IEnumerable<string> ids) =>
        Until(() => ids.All(id => _events.OfType<RunCompleted>().Any(e => e.ThreadId == id)), "every run's RunCompleted");

    public int EventCount(string id) => _events.Count(e => e.ThreadId == id);

    public async Task<int> CheckpointCountAsync(string id) => (await _store.GetLogAsync(id)).Entries.Count;

    public string Describe()
    {
        var snapshot = Scheduler.Snapshot();
        return $"active {snapshot.Active} of {snapshot.MaxConcurrent}, queued {snapshot.Queued} of {snapshot.MaxQueued}";
    }

    // The 10-second cap only stops a broken demo from hanging; nothing asserts how long it took.
    private static async Task Until(Func<bool> condition, string what)
    {
        var giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new InvalidOperationException($"gave up waiting for {what}");
            }

            await Task.Delay(5);
        }
    }

    private sealed class Observer(ConcurrentQueue<RunEvent> events) : IRunObserver
    {
        public void OnEvent(RunEvent runEvent) => events.Enqueue(runEvent);
    }
}
