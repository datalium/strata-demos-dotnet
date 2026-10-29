// Durable execution (design/33-Durable-Execution.md, SG-10): a checkpoint per node, and a real crash
// recovered.
//
// An order runs Validate -> Charge -> Ship, compiled with CheckpointPolicy.EveryNode, so the run writes a
// `running` checkpoint before its first node and after every node that commits, and `completed` at the
// end. With the default policy (Boundaries) a graph with no interrupt boundary writes nothing until it
// stops, and a process that dies mid-run leaves no trace of the thread at all.
//
// The crash is real. The program starts a copy of itself in `worker` mode. The worker runs the order
// under a lease, charges the card inside Charge, and then hangs before the node can return. The parent
// kills the worker's process at that moment, so Charge never commits, nothing releases the lease, and the
// thread is left `running` in a SQLite file. The parent then opens the same file and:
//
//   - shows that a `running` thread refuses an ordinary ResumeAsync;
//   - runs the recovery driver (RecoverStaleAsync), which skips the thread while the dead worker's lease
//     is live, and recovers it once the lease has expired;
//   - shows the contract: at least once. Charge runs again, with the same NodeContext.IdempotencyKey and
//     NodeContext.IsRecovery set, and the payment gateway, which deduplicates by that key, charges once.
//
// Both processes use one fake clock (FakeTimeProvider at the same instant), so the lease expires when the
// parent advances its clock, not after a real 30-second wait. Everything is deterministic and offline.
//
//   dotnet run --project demos/20-durable-execution/20-durable-execution.csproj
//
// The `worker <dir>` mode is the child's; you don't run it by hand.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

const string threadId = "order-1042";
const string crashMarker = "@@crash-here";
var ttl = TimeSpan.FromSeconds(30);
var noon = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

if (args is ["worker", var workerDir])
{
    return await RunWorkerAsync(workerDir);
}

if (args.Length > 0)
{
    Console.Error.WriteLine("usage: 20-durable-execution (no arguments; `worker <dir>` is the child process's mode)");
    return 2;
}

return await RunParentAsync(Path.Combine(Path.GetTempPath(), "strata-demo-20-durable-execution"));

// --- The child: run the order until Charge, where the parent kills it. ---

async Task<int> RunWorkerAsync(string dir)
{
    var files = new DemoFiles(dir);
    var clock = new FakeTimeProvider(noon);
    using var store = OpenStore(files.Checkpoints, clock);
    var graph = BuildGraph(new Journal(files.Visits, "worker-1", prefix: string.Empty), new PaymentGateway(files.Payments), hangInCharge: true);

    // The renewal timer runs on this clock, which never moves here: the lease simply runs out at 12:00:30.
    var options = new RunOptions { Lease = new LeaseOptions("worker-1", ttl), TimeProvider = clock };
    var result = await graph.RunAsync(threadId, new Order(threadId, AmountCents: 12900), store, options);

    // Only reached if Charge didn't hang: the parent treats a worker that exits on its own as a failure.
    Console.Error.WriteLine($"worker: the run returned instead of hanging in Charge: {result}");
    return 1;
}

// --- The parent: crash a worker, then recover what it left. ---

async Task<int> RunParentAsync(string dir)
{
    if (Directory.Exists(dir))
    {
        Directory.Delete(dir, recursive: true);
    }

    Directory.CreateDirectory(dir);
    var files = new DemoFiles(dir);
    var gateway = new PaymentGateway(files.Payments);

    Console.WriteLine("=== Step 1: a worker process runs the order and is killed inside Charge ===");
    await CrashWorkerAsync(dir);
    Console.WriteLine("  Killed the worker's process while Charge was still running: the node never committed.");

    Console.WriteLine();
    Console.WriteLine("=== Step 2: what the dead worker left in checkpoints.sqlite ===");
    var clock = new FakeTimeProvider(noon);
    using var store = OpenStore(files.Checkpoints, clock);
    var crashed = await LogAsync(store);
    PrintLog(crashed);
    Check(
        crashed.Select(Shape).SequenceEqual(["running:->Validate", "running:Validate>Charge"]),
        "expected a running checkpoint before Validate and one after it, and nothing after Charge started");
    var running = await ThreadsInAsync(store, CheckpointThreadState.Running);
    Console.WriteLine($"  Threads in state Running: {string.Join(", ", running)}");
    Check(running.SequenceEqual([threadId]), "expected the thread to be listed as running");
    var deadLease = await store.GetLeaseAsync(threadId);
    Console.WriteLine(
        $"  Lease: held by '{deadLease?.Owner}' until {Text(deadLease?.ExpiresAt)} (token {deadLease?.Token}): nobody released it.");
    Check(deadLease is { Owner: "worker-1", Token: 1 }, "expected the dead worker's lease to be still in place");
    Console.WriteLine($"  Payment gateway: {gateway.Requests} request, {gateway.Charges} charge.");
    Check((gateway.Requests, gateway.Charges) == (1, 1), "expected the worker to have charged the card before it died");

    Console.WriteLine();
    Console.WriteLine("=== Step 3: a running thread refuses an ordinary resume ===");
    var recoverer = BuildGraph(new Journal(files.Visits, "worker-2", prefix: "  | "), gateway, hangInCharge: false);
    var resumed = await recoverer.ResumeAsync(threadId, HumanSignal.Approve("ops-oncall"), store);
    var refusal = (resumed as GraphExecutionResult<Order>.Failed)?.Message;
    Console.WriteLine($"  ResumeAsync -> Failed: {refusal}");
    Check(
        refusal == $"thread '{threadId}' is running: only a waiting, failed or cancelled thread can be resumed",
        "expected a running thread to refuse a resume");
    Check((await LogAsync(store)).Count == crashed.Count, "expected the refused resume to write nothing");
    Console.WriteLine($"  Nothing was written: the log still has {crashed.Count} checkpoints. Only a recovery continues it.");

    Console.WriteLine();
    Console.WriteLine("=== Step 4: the recovery driver leaves a thread whose lease is live ===");
    var early = await recoverer.RecoverStaleAsync(
        store, new RecoverOptions { Run = new RunOptions { Lease = new LeaseOptions("worker-2", ttl), TimeProvider = clock } });
    Console.WriteLine($"  RecoverStaleAsync at {Text(clock.GetUtcNow())}: recovered {early.Recovered.Count}, skipped:");
    foreach (var skipped in early.Skipped)
    {
        Console.WriteLine($"    {skipped.ThreadId}: {skipped.Reason}");
    }

    Check(early.Recovered.Count == 0 && early.Skipped.Single().ThreadId == threadId, "expected the live lease to protect the thread");
    Console.WriteLine("  A live lease is the only sign of life: until it runs out, the worker might just be slow.");

    Console.WriteLine();
    Console.WriteLine("=== Step 5: the lease runs out, and the driver recovers the thread ===");
    clock.Advance(ttl + TimeSpan.FromSeconds(1));
    Console.WriteLine($"  Clock advanced to {Text(clock.GetUtcNow())}.");
    var events = new RunEventChannel();
    var report = await recoverer.RecoverStaleAsync(
        store,
        new RecoverOptions
        {
            // With a lease on the recovery runs, StaleAfter defaults to zero: a running thread nobody holds is dead.
            Run = new RunOptions { Lease = new LeaseOptions("worker-2", ttl), TimeProvider = clock, Observer = events },
        });
    var recovered = report.Recovered.Single();
    Console.WriteLine(
        $"  RecoverStaleAsync: recovered '{recovered.ThreadId}' at [{string.Join(", ", recovered.At)}], recoveries {recovered.Recoveries}, " +
        $"result {recovered.Result.GetType().Name}.");
    Check(recovered.At.SequenceEqual(["Charge"]) && recovered.Recoveries == 1, "expected the thread to continue at Charge, recovered once");
    var final = (recovered.Result as GraphExecutionResult<Order>.Completed)?.State
        ?? throw new InvalidOperationException($"expected the recovery to complete, got: {recovered.Result}");
    Console.WriteLine("  The recovery run's events:");
    var names = new List<string>();
    await foreach (var runEvent in events.Reader.ReadAllAsync())
    {
        names.Add(runEvent.GetType().Name);
        Console.WriteLine($"    {Describe(runEvent)}");
    }

    Check(
        names.Take(4).SequenceEqual(["RunStarted", "LeaseAcquired", "CheckpointSaved", "Recovered"]),
        "expected RunStarted, LeaseAcquired, the claim's CheckpointSaved and Recovered before the first node");

    Console.WriteLine();
    Console.WriteLine("=== Step 6: at least once, and charged once ===");
    Console.WriteLine("  Every node run, from both processes (visits.log):");
    var visits = Journal.Read(files.Visits);
    foreach (var visit in visits)
    {
        Console.WriteLine($"    {visit.Worker}  {visit.Node,-9} key {visit.Key,-22} IsRecovery {visit.IsRecovery}");
    }

    Check(
        visits.SequenceEqual(
            [
                new Visit("worker-1", "Validate", $"{threadId}/Validate#1", false),
                new Visit("worker-1", "Charge", $"{threadId}/Charge#2", false),
                new Visit("worker-2", "Charge", $"{threadId}/Charge#2", true),
                new Visit("worker-2", "Ship", $"{threadId}/Ship#3", false),
            ]),
        "expected Charge to run twice with one key, IsRecovery only on the re-run, and Validate not to run again");
    Console.WriteLine(
        $"  Payment gateway: {gateway.Requests} requests, {gateway.Charges} charge. The re-run sent the same key and got {final.ChargeId} back.");
    Check((gateway.Requests, gateway.Charges) == (2, 1), "expected two requests and one charge");
    Console.WriteLine($"  Final state: {final}");
    Check(final is { Validated: true, ChargeId: "ch_001", Shipped: true }, "expected the order validated, charged once and shipped");

    Console.WriteLine("  The whole checkpoint log, with RecoveryCount.Of(checkpoint):");
    var log = await LogAsync(store);
    PrintLog(log);
    Check(
        log.Select(Shape).SequenceEqual(
            ["running:->Validate", "running:Validate>Charge", "running:Validate>Charge", "running:Charge>Ship", "completed:Ship>-"]),
        "expected the claim (the crashed checkpoint again), a checkpoint after Charge, and completed");
    Check(log.Select(c => RecoveryCount.Of(c)).SequenceEqual([0, 0, 1, 0, 0]), "expected only the claim to count a recovery");
    Console.WriteLine("  Only the claim counts the recovery: the next node to commit starts the count again.");

    var lease = await store.GetLeaseAsync(threadId);
    var completed = await ThreadsInAsync(store, CheckpointThreadState.Completed);
    Console.WriteLine($"  Lease: {(lease is null ? "none (the recovery released it)" : $"held by '{lease.Owner}'")}. Threads in state Completed: {string.Join(", ", completed)}.");
    Check(lease is null && completed.SequenceEqual([threadId]), "expected the thread completed and its lease released");
    var again = await recoverer.RecoverAsync(threadId, store);
    Console.WriteLine($"  RecoverAsync again -> Failed: {(again as GraphExecutionResult<Order>.Failed)?.Message}");
    Check(
        (again as GraphExecutionResult<Order>.Failed)?.Message == $"thread '{threadId}' is not running: its latest checkpoint is completed",
        "expected a completed thread to refuse a recovery");

    Console.WriteLine();
    Console.WriteLine($"Files left in {dir}");
    Console.WriteLine("=== Done: the process died mid-node, and the thread finished without charging twice. ===");
    return 0;
}

// Starts this program in `worker` mode, relays its output, and kills it when it says it is inside Charge.
async Task CrashWorkerAsync(string dir)
{
    var self = Environment.ProcessPath ?? throw new InvalidOperationException("can't tell where this program is");
    var start = new ProcessStartInfo(self)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    // Run as `dotnet 20-durable-execution.dll`, the host is dotnet itself and needs the assembly first.
    if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(typeof(Order).Assembly.Location);
    }

    start.ArgumentList.Add("worker");
    start.ArgumentList.Add(dir);

    using var worker = Process.Start(start) ?? throw new InvalidOperationException("couldn't start the worker");
    var stderr = worker.StandardError.ReadToEndAsync();

    // A worker that never reaches Charge is a failure, not a reason to wait forever.
    using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var killed = false;
    try
    {
        while (await worker.StandardOutput.ReadLineAsync(patience.Token) is { } line)
        {
            if (line == crashMarker)
            {
                worker.Kill(entireProcessTree: true);
                killed = true;
                break;
            }

            Console.WriteLine($"  | {line}");
        }
    }
    catch (OperationCanceledException)
    {
        worker.Kill(entireProcessTree: true);
    }

    await worker.WaitForExitAsync();
    if (!killed)
    {
        throw new InvalidOperationException($"the worker didn't reach Charge: {await stderr}");
    }
}

static CompiledGraph<Order> BuildGraph(Journal journal, PaymentGateway gateway, bool hangInCharge) =>
    new StateGraph<Order>()
        .AddNode("Validate", (order, context, _) =>
        {
            journal.Visit(context);
            return ValueTask.FromResult(order with { Validated = true });
        })
        .AddNode("Charge", async (order, context, _) =>
        {
            journal.Visit(context);

            // The effect outside the state: the gateway deduplicates by the key, as real payment APIs do.
            var charge = gateway.Charge(context.IdempotencyKey, order.AmountCents);
            journal.Say(charge.Repeated
                ? $"{journal.Worker}: Charge: the gateway already had key {context.IdempotencyKey}: returned {charge.Id}, charged nothing"
                : $"{journal.Worker}: Charge: the gateway charged {charge.Id} (${(order.AmountCents / 100m).ToString("0.00", CultureInfo.InvariantCulture)}) under key {context.IdempotencyKey}");
            if (hangInCharge)
            {
                // The crash point: the card is charged, the node hasn't returned, so nothing records it.
                journal.Say($"{journal.Worker}: Charge: hangs before it can return (the parent kills the process now)");
                Console.WriteLine(crashMarker);
                await Task.Delay(Timeout.Infinite, CancellationToken.None);
            }

            return order with { ChargeId = charge.Id };
        })
        .AddNode("Ship", (order, context, _) =>
        {
            journal.Visit(context);
            return ValueTask.FromResult(order with { Shipped = true });
        })
        .AddEdge("Validate", "Charge")
        .AddEdge("Charge", "Ship")
        .Compile(new CompilationOptions
        {
            GraphId = "order-fulfilment",
            GraphVersion = "1",
            Checkpointing = CheckpointPolicy.EveryNode,
        });

static EfCoreCheckpointStore OpenStore(string path, TimeProvider clock)
{
    var options = new DbContextOptionsBuilder<CheckpointDbContext>().UseSqlite($"Data Source={path}").Options;

    // The store's clock is the one lease expiry is judged by, in every process that opens the file.
    return new EfCoreCheckpointStore(new CheckpointDbContext(options), timeProvider: clock);
}

static async Task<List<Checkpoint>> LogAsync(EfCoreCheckpointStore store) =>
    [.. (await store.GetLogAsync(threadId)).Entries.Select(e => e.Checkpoint)];

static async Task<List<string>> ThreadsInAsync(EfCoreCheckpointStore store, CheckpointThreadState state) =>
    [.. (await store.ListThreadsAsync(new ThreadQuery { States = new HashSet<CheckpointThreadState> { state } }))
        .Threads.Select(t => t.ThreadId)];

static void PrintLog(List<Checkpoint> log)
{
    for (var i = 0; i < log.Count; i++)
    {
        var c = log[i];
        Console.WriteLine(
            $"    #{i + 1}  {Status(c.Status),-9}  after {c.SuspendedNode ?? "-",-9} next {c.NextNode ?? "-",-8}  recoveries {RecoveryCount.Of(c)}");
    }
}

// `running:Validate>Charge`: the status, the node that last committed, the node to run next.
static string Shape(Checkpoint c) => $"{Status(c.Status)}:{c.SuspendedNode ?? "-"}>{c.NextNode ?? "-"}";

static string Status(CheckpointStatus status) => status.ToString().ToLowerInvariant();

static string Text(DateTimeOffset? time) =>
    time?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? "-";

static string Describe(RunEvent e) => e switch
{
    RunStarted s => $"RunStarted       mode {s.Mode}, at {s.Node}",
    LeaseAcquired l => $"LeaseAcquired    '{l.Owner}', token {l.Token}, until {Text(l.ExpiresAt)}",
    CheckpointSaved c => $"CheckpointSaved  {Status(c.Status)} at [{string.Join(", ", c.WaitingAt)}]",
    Recovered r => $"Recovered        at [{string.Join(", ", r.At)}], recoveries {r.Recoveries}",
    NodeStarted => $"NodeStarted      {e.NodePath}",
    NodeCompleted n => $"NodeCompleted    {e.NodePath}: {n.Result}",
    RunCompleted r => $"RunCompleted     {r.Outcome}",
    _ => e.GetType().Name,
};

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Order(
    [property: JsonPropertyName("order_id")] string OrderId,
    [property: JsonPropertyName("amount_cents")] long AmountCents,
    [property: JsonPropertyName("validated")] bool Validated = false,
    [property: JsonPropertyName("charge_id")] string? ChargeId = null,
    [property: JsonPropertyName("shipped")] bool Shipped = false);

/// <summary>Where the two processes meet: the checkpoint store and two plain logs, all in one folder.</summary>
sealed record DemoFiles(string Dir)
{
    public string Checkpoints => Path.Combine(Dir, "checkpoints.sqlite");

    public string Payments => Path.Combine(Dir, "payments.log");

    public string Visits => Path.Combine(Dir, "visits.log");
}

sealed record Visit(string Worker, string Node, string Key, bool IsRecovery);

/// <summary>
/// A record of every node run, appended to a file so it survives the worker's death. Each append is closed
/// before the node goes on, so a kill can't lose it.
/// </summary>
sealed class Journal(string path, string worker, string prefix)
{
    public string Worker => worker;

    /// <summary>A node's output: the worker's goes through the parent, which adds the same <c>  | </c> prefix.</summary>
    public void Say(string line) => Console.WriteLine(prefix + line);

    public void Visit(NodeContext context)
    {
        File.AppendAllLines(path, [$"{worker}\t{context.NodePath}\t{context.IdempotencyKey}\t{context.IsRecovery}"]);
        Say($"{worker}: {context.NodePath} (key {context.IdempotencyKey}, IsRecovery {context.IsRecovery})");
    }

    public static List<Visit> Read(string path) =>
        [.. File.ReadAllLines(path).Select(line => line.Split('\t')).Select(f => new Visit(f[0], f[1], f[2], bool.Parse(f[3])))];
}

/// <summary>
/// A stand-in for a payment provider that takes an idempotency key: a request with a key it has seen
/// returns the first charge instead of making another. Backed by a file, so it outlives either process.
/// </summary>
sealed class PaymentGateway(string path)
{
    public int Requests => Lines().Count(l => l[0] == "request");

    public int Charges => Lines().Count(l => l[0] == "charge");

    public (string Id, bool Repeated) Charge(string idempotencyKey, long amountCents)
    {
        File.AppendAllLines(path, [$"request\t{idempotencyKey}"]);
        var earlier = Lines().FirstOrDefault(l => l[0] == "charge" && l[1] == idempotencyKey);
        if (earlier is not null)
        {
            return (earlier[2], true);
        }

        var id = $"ch_{Charges + 1:000}";
        File.AppendAllLines(path, [$"charge\t{idempotencyKey}\t{id}\t{amountCents}"]);
        return (id, false);
    }

    private List<string[]> Lines() =>
        File.Exists(path) ? [.. File.ReadAllLines(path).Select(line => line.Split('\t'))] : [];
}
