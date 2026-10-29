// Graph identity (design/31-Graph-Identity.md): deploying a new version of a graph while threads
// the old one wrote are still waiting.
//
// A shop's returns process: Inspect -> Approve (a manager's sign-off) -> Refund. Returns wait at
// Approve for days, so when version 2 ships, some threads version 1 wrote are still suspended in
// the store. Version 2 renames a state field (Amount -> RefundAmount), adds one (RefundMethod),
// renames a node (Refund -> IssueRefund) and adds a node after it (Restock).
//
// A graph compiled with CompilationOptions.GraphId / GraphVersion writes that identity on every
// checkpoint, and refuses a thread another graph or another version wrote, before writing
// anything, unless its CompilationOptions.Migrate adopts it. Everything runs against one SQLite
// file, read back directly to show the identity is stored in its own columns.

using System.Globalization;
using System.Text.Json.Nodes;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// bin/ is gitignored, so the database can live next to the binary. Deleted first so every run
// starts from an empty file.
var dbPath = Path.Combine(AppContext.BaseDirectory, "returns.db");
if (File.Exists(dbPath))
{
    File.Delete(dbPath);
}

var stepCount = 0;
var approve = HumanSignal.Approve("manager-ines");

var v1 = ReturnsV1().Compile(new CompilationOptions { GraphId = "returns", GraphVersion = "1" });
var v2 = ReturnsV2().Compile(new CompilationOptions { GraphId = "returns", GraphVersion = "2" });

// --- Step 1: version 1 is live. Two returns wait for a manager. ---

Step("Step 1: version 1 is deployed; two returns wait for approval");
using (var store = Open())
{
    foreach (var (id, amount) in new[] { ("R-1", 120m), ("R-2", 75m) })
    {
        var result = await v1.RunAsync(id, ReturnV1.New(id, amount), store);
        Check(result is GraphExecutionResult<ReturnV1>.Interrupted { SuspendedNode: "Approve" }, $"{id} should wait at Approve");
        Console.WriteLine($"  {id} (${Money(amount)}) waits at Approve, written by {store.Load(id)!.Graph}");
    }
}

// --- Step 2: version 2 ships, with no migration. It runs new returns and refuses old ones. ---

Step("Step 2: version 2 is deployed, with no migration");
using (var store = Open())
{
    var fresh = await v2.RunAsync("R-3", ReturnV2.New("R-3", 40m), store);
    Check(fresh is GraphExecutionResult<ReturnV2>.Interrupted, "R-3 should wait at Approve");
    Console.WriteLine($"  R-3 ($40) is new: version 2 runs it, and it waits at Approve, written by {store.Load("R-3")!.Graph}");

    PrintThreads();

    var before = Rows();
    var refused = await v2.ResumeAsync("R-1", approve, store);
    Console.WriteLine($"  Version 2 resumes R-1: {Refused(refused)}");
    Check(
        Refused(refused) == "thread 'R-1' was written by graph 'returns' version 1, not version 2, and this graph has no migration",
        "version 2 should refuse a version 1 thread");
    Console.WriteLine($"  Checkpoint rows before and after: {before} and {Rows()}. R-1 still waits, resumable by version 1.");
    Check(Rows() == before, "a refused resume should write nothing");

    // Owns is the cheap pre-check a worker can make before resuming anything.
    Console.WriteLine($"  v2.Owns(R-1): {v2.Owns(store.Load("R-1")!)}, v2.Owns(R-3): {v2.Owns(store.Load("R-3")!)}");
    Check(!v2.Owns(store.Load("R-1")!) && v2.Owns(store.Load("R-3")!), "v2 should own its own version's threads only");
}

// --- Step 3: another graph, even with the same node names and state, is refused. ---

Step("Step 3: a different graph can't take a returns thread");
using (var store = Open())
{
    var before = Rows();

    // Same builder, same nodes, same state type: only the identity differs.
    var exchanges = ReturnsV1().Compile(new CompilationOptions { GraphId = "exchanges", GraphVersion = "1" });
    var other = await exchanges.ResumeAsync("R-1", approve, store);
    Console.WriteLine($"  'exchanges' resumes R-1:   {Refused(other)}");
    Check(Refused(other) == "thread 'R-1' was written by graph 'returns' version 1, not 'exchanges' version 1", "another id should be refused");

    var unnamed = ReturnsV1().Compile();
    var anonymous = await unnamed.ResumeAsync("R-1", approve, store);
    Console.WriteLine($"  An unnamed graph resumes R-1: {Refused(anonymous)}");
    Check(Refused(anonymous).EndsWith("and this graph has no identity", StringComparison.Ordinal), "a graph without an identity should be refused");

    var end = await exchanges.EndAsync("R-1", store);
    Console.WriteLine($"  'exchanges' ends R-1:      {Refused(end)}");
    Check(Refused(end) == Refused(other), "an end should be refused like a resume");
    Check(Rows() == before, "refusals should write nothing");
}

// --- Step 4: which threads still need version 1? Drain them with version 1. ---

Step("Step 4: the query port lists each version's waiting threads");
using (var store = Open())
{
    var v1Waiting = await Waiting(store, "1");
    var v2Waiting = await Waiting(store, "2");
    Console.WriteLine($"  returns version 1, interrupted: {string.Join(", ", v1Waiting)}");
    Console.WriteLine($"  returns version 2, interrupted: {string.Join(", ", v2Waiting)}");
    Check(v1Waiting.SequenceEqual(["R-1", "R-2"]) && v2Waiting.SequenceEqual(["R-3"]), "unexpected listing by version");

    // Version 1 stays deployed beside version 2 for a while. A dispatcher picks the runner by the
    // identity stored on the thread's latest checkpoint.
    var latest = await store.LoadLatestAsync("R-1");
    Console.WriteLine($"  R-1 was written by version {latest!.Checkpoint.Graph!.Version}, so it goes to version 1:");
    var drained = await v1.ResumeAsync("R-1", approve, store);
    var refunded = Expect<GraphExecutionResult<ReturnV1>.Completed>(drained, "R-1").State;
    Console.WriteLine($"    completed: {string.Join(" / ", refunded.Log)}");
    Check(refunded.Refunded, "version 1 should refund R-1");
}

// --- Step 5: version 2 gets a migration, and adopts the last version 1 thread. ---

Step("Step 5: version 2 is redeployed with a migration for version 1 threads");
var migrations = new List<string>();
var v2Migrating = ReturnsV2().Compile(new CompilationOptions
{
    GraphId = "returns",
    GraphVersion = "2",
    Migrate = checkpoint =>
    {
        // It sees the level as stored. Anything but version 1 is refused (null).
        if (checkpoint.Graph?.Version != "1")
        {
            return null;
        }

        migrations.Add(checkpoint.ThreadId);
        var state = checkpoint.FrozenState!.DeepClone().AsObject();
        state["RefundAmount"] = state["Amount"]!.DeepClone();
        state.Remove("Amount");
        state["RefundMethod"] = "original-payment";
        state["Restocked"] = false;
        return checkpoint with
        {
            FrozenState = state,
            NextNode = checkpoint.NextNode == "Refund" ? "IssueRefund" : checkpoint.NextNode,
        };
    },
});
using (var store = Open())
{
    Console.WriteLine($"  v2.Owns(R-2) without the migration: {v2.Owns(store.Load("R-2")!)}; with it: {v2Migrating.Owns(store.Load("R-2")!)}");
    Check(v2Migrating.Owns(store.Load("R-2")!), "a graph with a migration should own other versions of its id");

    var adopted = await v2Migrating.ResumeAsync("R-2", approve, store);
    var state = Expect<GraphExecutionResult<ReturnV2>.Completed>(adopted, "R-2").State;
    Console.WriteLine($"  Version 2 resumes R-2: completed: {string.Join(" / ", state.Log)}");
    Check(migrations.SequenceEqual(["R-2"]), "the migration should run once, for R-2");
    Check(
        state is { RefundAmount: 75m, RefundMethod: "original-payment", Refunded: true, Restocked: true },
        "R-2 should be refunded and restocked by version 2, with its amount carried over");

    // Version 2's own threads need no migration.
    var own = await v2Migrating.ResumeAsync("R-3", approve, store);
    Check(Expect<GraphExecutionResult<ReturnV2>.Completed>(own, "R-3").State.Restocked, "R-3 should complete");
    Check(migrations.Count == 1, "a version 2 thread should not be migrated");

    Console.WriteLine("  R-2's rows in the file: the resume stamp carries the migrated identity, state and next node:");
    foreach (var (sequence, version, nextNode, frozen) in RowsOf("R-2"))
    {
        Console.WriteLine($"    #{sequence} graph_version {version}  next_node {nextNode,-11}  frozen_state {frozen}");
    }

    var rows = RowsOf("R-2");
    Check(rows.Count == 2 && rows[0].Version == "1" && rows[1].Version == "2", "the stamp should record version 2");
    Check(rows[1].NextNode == "IssueRefund" && rows[1].FrozenState.Contains("\"RefundAmount\":75", StringComparison.Ordinal), "the stamp should be the migrated checkpoint");

    var v1Waiting = await Waiting(store, "1");
    Console.WriteLine($"  returns version 1, interrupted: {(v1Waiting.Count == 0 ? "none: version 1 can be retired" : string.Join(", ", v1Waiting))}");
    Check(v1Waiting.Count == 0, "no version 1 thread should be left waiting");
}

Step("Done: what the file holds now");
PrintThreads();

// --- The graphs ---

static StateGraph<ReturnV1> ReturnsV1() =>
    new StateGraph<ReturnV1>()
        .AddNode("Inspect", r => (r with { Inspected = true }).Note("inspected"))
        .AddNode("Approve", r => r.Note("sent for approval"))
        .AddInterruptBoundary("Approve")
        .AddNode("Refund", r => (r with { Refunded = true }).Note($"refunded ${Money(r.Amount)}"))
        .AddEdge("Inspect", "Approve")
        .AddEdge("Approve", "Refund");

static StateGraph<ReturnV2> ReturnsV2() =>
    new StateGraph<ReturnV2>()
        .AddNode("Inspect", r => (r with { Inspected = true }).Note("inspected"))
        .AddNode("Approve", r => r.Note("sent for approval"))
        .AddInterruptBoundary("Approve")
        .AddNode("IssueRefund", r => (r with { Refunded = true }).Note($"refunded ${Money(r.RefundAmount)} to {r.RefundMethod}"))
        .AddNode("Restock", r => (r with { Restocked = true }).Note("restocked"))
        .AddEdge("Inspect", "Approve")
        .AddEdge("Approve", "IssueRefund")
        .AddEdge("IssueRefund", "Restock");

// --- Helpers ---

EfCoreCheckpointStore Open() =>
    new(new CheckpointDbContext(new DbContextOptionsBuilder<CheckpointDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options));

static async Task<List<string>> Waiting(EfCoreCheckpointStore store, string version)
{
    var page = await store.ListThreadsAsync(new ThreadQuery
    {
        GraphId = "returns",
        GraphVersion = version,
        States = new HashSet<CheckpointThreadState> { CheckpointThreadState.Interrupted },
    });

    // The listing is newest first, by wall-clock time; sort for stable output.
    return [.. page.Threads.Select(t => t.ThreadId).Order(StringComparer.Ordinal)];
}

// Reads the file directly, with no Strata code, to show the identity is stored.
SqliteConnection Raw()
{
    var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
    connection.Open();
    return connection;
}

long Rows()
{
    using var connection = Raw();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM checkpoints";
    return (long)command.ExecuteScalar()!;
}

void PrintThreads()
{
    using var connection = Raw();
    using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT h.thread_id, h.state, h.graph_id, h.graph_version, c.schema_version
        FROM checkpoint_threads h
        JOIN checkpoints c ON c.thread_id = h.thread_id AND c.sequence = h.sequence
        ORDER BY h.thread_id
        """;
    using var reader = command.ExecuteReader();
    Console.WriteLine("    checkpoint_threads (the head table the query port reads):");
    while (reader.Read())
    {
        Console.WriteLine(
            $"      {reader.GetString(0)}  {reader.GetString(1),-11}  graph_id {reader.GetString(2)}  graph_version {reader.GetString(3)}  schema_version {reader.GetString(4)}");
    }
}

List<(long Sequence, string Version, string NextNode, string FrozenState)> RowsOf(string threadId)
{
    using var connection = Raw();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT sequence, graph_version, next_node, frozen_state FROM checkpoints WHERE thread_id = $id ORDER BY sequence";
    command.Parameters.AddWithValue("$id", threadId);
    using var reader = command.ExecuteReader();
    var rows = new List<(long, string, string, string)>();
    while (reader.Read())
    {
        rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    }

    return rows;
}

static string Money(decimal amount) => amount.ToString("0", CultureInfo.InvariantCulture);

void Step(string title)
{
    if (stepCount++ > 0)
    {
        Console.WriteLine();
    }

    Console.WriteLine($"=== {title} ===");
}

static T Expect<T>(object result, string threadId)
    where T : class =>
    result as T ?? throw new InvalidOperationException($"{threadId}: expected {typeof(T).Name}, got {result}");

static string Refused(object result) => result switch
{
    GraphExecutionResult<ReturnV1>.Failed f => f.Message,
    GraphExecutionResult<ReturnV2>.Failed f => f.Message,
    _ => throw new InvalidOperationException($"expected a refusal, got {result}"),
};

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

// Version 1's state.
sealed record ReturnV1(string ReturnId, decimal Amount, bool Inspected, bool Refunded, IReadOnlyList<string> Log)
{
    public static ReturnV1 New(string returnId, decimal amount) => new(returnId, amount, false, false, []);

    public ReturnV1 Note(string line) => this with { Log = [.. Log, line] };
}

// Version 2's state: Amount is now RefundAmount, and RefundMethod and Restocked are new.
sealed record ReturnV2(
    string ReturnId,
    decimal RefundAmount,
    string RefundMethod,
    bool Inspected,
    bool Refunded,
    bool Restocked,
    IReadOnlyList<string> Log)
{
    public static ReturnV2 New(string returnId, decimal amount) =>
        new(returnId, amount, "original-payment", false, false, false, []);

    public ReturnV2 Note(string line) => this with { Log = [.. Log, line] };
}
