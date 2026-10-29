// Event waits, durable timers and ending a thread (design/29-Event-Waits-And-Timers.md,
// design/28-Ending-A-Thread.md, design/42-Waking-Through-A-Resume.md).
//
// An online shop's order process, which spends almost all of its life waiting:
//
//   Place -> AwaitPayment (an event wait: "payment.received", at most 24 h, no timeout route)
//         -> FraudReview, for orders of $1000 or more (a human boundary: at most 2 h, then Escalate)
//         -> Pack -> AwaitPickup (a 4 h timer) -> Ship
//
// While an order waits, no process holds it. Its thread is a few rows in a SQLite file, and every
// step below opens a fresh store on that file, the way a webhook handler, a cron job and a support
// tool would, each in its own process. Nothing waits in memory between steps.
//
// A FakeTimeProvider passed as RunOptions.TimeProvider is the clock: it decides each wait's wake_at
// and what "now" means to WakeDueAsync and LevelSignals.Due. The demo moves it by hand, so a day of
// waiting takes no time and every wake_at below is exact. Checkpoint created_at stays wall-clock, so
// the demo never prints it.

using System.Globalization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

// bin/ is gitignored, so the database can live next to the binary. Deleted first so every run
// starts from an empty file.
var dbPath = Path.Combine(AppContext.BaseDirectory, "orders.db");
if (File.Exists(dbPath))
{
    File.Delete(dbPath);
}

var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-02T09:00:00Z", CultureInfo.InvariantCulture));
var run = new RunOptions { TimeProvider = clock };
var wake = new WakeOptions { Run = run };
var orders = BuildOrderGraph();
var stepCount = 0;

// --- Step 1: five orders are placed, and each waits for its payment. ---

Step("Step 1: five orders are placed and wait for payment");
var placed = new (string Id, decimal Total)[]
{
    ("order-1001", 80m),
    ("order-1002", 45m),
    ("order-1003", 2400m),
    ("order-1004", 60m),
    ("order-1005", 35m),
};
using (var store = Open())
{
    foreach (var (id, total) in placed)
    {
        var result = await orders.RunAsync(id, Order.New(id, total), store, run);
        var waiting = Expect<GraphExecutionResult<Order>.Interrupted>(result, id);
        Console.WriteLine($"  {id} (${Money(total)}): {Describe(waiting.Wait)}");
        Check(
            waiting.Wait == new CheckpointWait(WaitKind.Event, "payment.received", "2026-11-03T09:00:00Z"),
            $"{id} should wait for payment.received until the next morning");
    }

    // The edge out of a wait is chosen when it freezes, on the state as it was then, so the
    // high-value order is already headed for FraudReview before anyone has paid.
    var highValue = store.Load("order-1003")!;
    Console.WriteLine($"  order-1003's next node, fixed at the freeze: {highValue.NextNode}");
    Check(highValue.NextNode == "FraudReview", "order-1003 should be routed to FraudReview");
}

Console.WriteLine("  The store is disposed. What is left of the five orders:");
PrintHeads();
Console.WriteLine($"  order-1001's wait column: {Scalar("SELECT wait FROM checkpoints WHERE thread_id = 'order-1001'")}");
Check(Rows() == 5, "expected one checkpoint row per order");

// --- Step 2: the wrong answer to a wait is refused, and writes nothing. ---

Step("Step 2: the wrong answers to a payment wait are refused");
using (var store = Open())
{
    var before = Rows();

    var approved = await orders.ResumeAsync("order-1001", HumanSignal.Approve("clerk"), store, run);
    Console.WriteLine($"  A clerk approves it:      {Refused(approved)}");
    Check(Refused(approved).Contains("only a signal of that event answers it"), "a human resume of an event wait should be refused");

    var refund = await orders.SignalAsync("order-1001", new EventSignal("refund.issued"), store, run);
    Console.WriteLine($"  A refund.issued event:    {Refused(refund)}");
    Check(Refused(refund).Contains("is not waiting for event 'refund.issued'"), "a signal of another event type should be refused");

    var early = await orders.ResumeAsync("order-1001", LevelSignals.Due, store, run);
    Console.WriteLine($"  A wake before it is due:  {Refused(early)}");
    Check(Refused(early).Contains("is not due until 2026-11-03T09:00:00Z"), "a wake before the timeout should be refused");

    Console.WriteLine($"  Checkpoint rows before and after: {before} and {Rows()}.");
    Check(Rows() == before, "a refused answer should write nothing");
}

// --- Step 3: 09:20, the payment provider's webhook signals three orders. ---

clock.Advance(TimeSpan.FromMinutes(20));
Step($"Step 3: {Now()}, payments arrive for order-1001, order-1003 and order-1005");
using (var store = Open())
{
    foreach (var (id, reference) in new[] { ("order-1001", "PAY-5531"), ("order-1003", "PAY-5532"), ("order-1005", "PAY-5533") })
    {
        // The signal's overrides replace top-level fields of the waiting state: the payload is in
        // the state from here on, in every later node and checkpoint.
        var signal = new EventSignal("payment.received").WithOverride("Paid", true).WithOverride("PaymentRef", reference);
        var result = await orders.SignalAsync(id, signal, store, run);
        var waiting = Expect<GraphExecutionResult<Order>.Interrupted>(result, id);
        Console.WriteLine($"  {id}: paid ({waiting.FrozenState.PaymentRef}), now {Describe(waiting.Wait)}");
        Check(waiting.FrozenState.Paid && waiting.FrozenState.PaymentRef == reference, $"{id} should carry its payment");
    }

    Check(
        store.Load("order-1001")!.Wait == new CheckpointWait(WaitKind.Timer, null, "2026-11-02T13:20:00Z"),
        "order-1001 should wait for the carrier until 13:20");
    Check(
        store.Load("order-1003")!.Wait == new CheckpointWait(WaitKind.Human, null, "2026-11-02T11:20:00Z"),
        "order-1003 should wait for a reviewer until 11:20");
}

// --- Step 4: 09:30, a customer cancels an unpaid order. ---

clock.Advance(TimeSpan.FromMinutes(10));
Step($"Step 4: {Now()}, the customer cancels order-1004 before paying");
using (var store = Open())
{
    var ended = Expect<GraphExecutionResult<Order>.Cancelled>(
        await orders.EndAsync("order-1004", ThreadEnd.By("customer"), store, run), "order-1004");
    var checkpoint = store.Load("order-1004")!;
    Console.WriteLine(
        $"  Ended at {ended.Node} ({ended.Reason}): status {checkpoint.Status}, {ThreadEnd.ReasonKey}={checkpoint.Metadata![ThreadEnd.ReasonKey]}, {ThreadEnd.EndedByKey}={checkpoint.Metadata[ThreadEnd.EndedByKey]}");
    Check(ended.Reason == CancelReason.Ended && ended.Node == "AwaitPayment", "order-1004 should end where it waited");
    Check(checkpoint.Metadata[ThreadEnd.ReasonKey] == ThreadEnd.Requested, "a caller's end should record 'requested'");
    Check(checkpoint.Wait is null, "an ended thread waits for nothing");

    var late = await orders.SignalAsync("order-1004", new EventSignal("payment.received"), store, run);
    Console.WriteLine($"  A late payment for it:    {Refused(late)}");
    Check(Refused(late).Contains("was ended"), "a signal to an ended thread should be refused");
}

// --- Step 5: 12:00, the wake driver runs. Only the fraud review is due. ---

clock.SetUtcNow(DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture));
Step($"Step 5: {Now()}, the wake driver runs");
using (var store = Open())
{
    var due = await DueNow(store);
    Console.WriteLine($"  Due now (ThreadQuery.DueBy): {string.Join(", ", due)}");
    Check(due.SequenceEqual(["order-1003"]), "only the unanswered fraud review should be due");

    var report = await orders.WakeDueAsync(store, wake);
    foreach (var woken in report.Woken)
    {
        Console.WriteLine($"  Woke {woken.ThreadId} ({woken.Reason}): {Outcome(woken.Result)}");
    }

    var escalated = Single(report.Woken);
    Check(escalated is { ThreadId: "order-1003", Reason: WakeReason.Timeout }, "order-1003's review should time out");
    var state = Expect<GraphExecutionResult<Order>.Completed>(escalated.Result, "order-1003").State;
    Check(state is { Review: "escalated", Shipped: false }, "a timed-out review should take its route to Escalate");
    Check(store.Load("order-1003")!.Decision is null, "a timeout is not a human's answer: no decision");
}

// --- Step 6: 13:30, the pickup timers are due. The application wakes one itself, streaming. ---

clock.SetUtcNow(DateTimeOffset.Parse("2026-11-02T13:30:00Z", CultureInfo.InvariantCulture));
Step($"Step 6: {Now()}, the carrier is here: both pickup timers are due");
using (var store = Open())
{
    var due = await DueNow(store);
    Console.WriteLine($"  Due now: {string.Join(", ", due)}");
    Check(due.SequenceEqual(["order-1001", "order-1005"]), "both pickup timers should be due");

    // WakeDueAsync runs each thread as a plain resume and doesn't stream. An application that
    // wants a woken run's chunks (or its own options per thread) resumes it with LevelSignals.Due,
    // which decides at its own load that the thread is still due.
    Console.WriteLine("  The application wakes order-1005 through its own ResumeStreaming(LevelSignals.Due):");
    var (chunks, completion) = orders.ResumeStreaming("order-1005", LevelSignals.Due, store, run);
    var seen = new List<string>();
    await foreach (var chunk in chunks.ReadAllAsync())
    {
        seen.Add($"{chunk.Type}:{chunk.NodeName}");
        Console.WriteLine($"    chunk {chunk.Type,-9} {chunk.NodeName}");
    }

    var shipped = Expect<GraphExecutionResult<Order>.Completed>(await completion, "order-1005").State;
    Console.WriteLine($"    -> {Outcome(new GraphExecutionResult<Order>.Completed(shipped))}");
    Check(seen.SequenceEqual(["NodeStart:Ship", "NodeEnd:Ship"]), "the woken run should stream from the node after the timer");
    Check(shipped.Shipped, "order-1005 should ship");

    // The driver then finds only the other one: order-1005 is no longer due.
    var report = await orders.WakeDueAsync(store, wake);
    foreach (var woken in report.Woken)
    {
        Console.WriteLine($"  The wake driver woke {woken.ThreadId} ({woken.Reason}): {Outcome(woken.Result)}");
    }

    Check(Single(report.Woken) is { ThreadId: "order-1001", Reason: WakeReason.Timer }, "the driver should wake order-1001 only");
}

// --- Step 7: the next morning, order-1002's payment window has closed. ---

clock.SetUtcNow(DateTimeOffset.Parse("2026-11-03T09:00:00Z", CultureInfo.InvariantCulture));
Step($"Step 7: {Now()}, order-1002 was never paid");
using (var store = Open())
{
    var report = await orders.WakeDueAsync(store, wake);
    var expired = Single(report.Woken);
    var ended = Expect<GraphExecutionResult<Order>.Cancelled>(expired.Result, expired.ThreadId);
    Console.WriteLine($"  Woke {expired.ThreadId} ({expired.Reason}): {Outcome(expired.Result)}");
    Check(expired is { ThreadId: "order-1002", Reason: WakeReason.Timeout }, "order-1002's payment wait should time out");
    Check(ended is { Reason: CancelReason.Ended, Node: "AwaitPayment" }, "a timeout with no route should end the thread");

    // The engine's end records why, where the query port can filter on it.
    var timedOut = await store.ListThreadsAsync(new ThreadQuery
    {
        Metadata = new Dictionary<string, string> { [ThreadEnd.ReasonKey] = ThreadEnd.TimedOut },
    });
    var ids = timedOut.Threads.Select(t => t.ThreadId).ToList();
    Console.WriteLine($"  Threads with {ThreadEnd.ReasonKey}={ThreadEnd.TimedOut}: {string.Join(", ", ids)}");
    Check(ids.SequenceEqual(["order-1002"]), "only order-1002 should have timed out");

    Check((await DueNow(store)).Count == 0, "nothing should be due any more");
}

Step("Done: what the file holds now");
PrintHeads();
Console.WriteLine($"  {Rows()} checkpoint rows in all. A completed run writes nothing, so a finished order's head is its last resume stamp.");
Console.WriteLine("  The stamp keeps the wake_at of the wait it answered; only an interrupted, unresumed thread is due.");
Check(Rows() == 16, "expected 16 checkpoint rows");
Check(
    Heads().SequenceEqual(
    [
        ("order-1001", "resumed", "2026-11-02T13:20:00Z"),
        ("order-1002", "ended", null),
        ("order-1003", "resumed", "2026-11-02T11:20:00Z"),
        ("order-1004", "ended", null),
        ("order-1005", "resumed", "2026-11-02T13:20:00Z"),
    ]),
    "unexpected head table");

// --- The graph ---

// AwaitPayment has no timeout route: an order nobody pays for is ended, timed_out, where it waits.
static CompiledGraph<Order> BuildOrderGraph() =>
    new StateGraph<Order>()
        .AddNode("Place", o => o.Note($"placed for ${Money(o.Total)}"))
        .AddWaitForEvent("AwaitPayment", "payment.received", timeout: TimeSpan.FromHours(24))
        .AddNode("FraudReview", o => o.Note("held for a fraud review"))
        .AddInterruptBoundary("FraudReview", onReject: "Refund", timeout: TimeSpan.FromHours(2), onTimeout: "Escalate")
        .AddNode("Pack", o => o.Note($"packed, payment {o.PaymentRef}"))
        .AddTimer("AwaitPickup", TimeSpan.FromHours(4))
        .AddNode("Ship", o => (o with { Shipped = true }).Note("handed to the carrier"))
        .AddNode("Escalate", o => (o with { Review = "escalated" }).Note("not reviewed within 2 h: escalated to the risk team"))
        .AddNode("Refund", o => (o with { Review = "rejected" }).Note("refunded"))
        .AddEdge("Place", "AwaitPayment")
        .AddConditionalEdge("AwaitPayment", "FraudReview", o => o.Total >= 1000m)
        .AddEdge("AwaitPayment", "Pack")
        .AddEdge("FraudReview", "Pack")
        .AddEdge("Pack", "AwaitPickup")
        .AddEdge("AwaitPickup", "Ship")
        .Compile();

// --- Helpers ---

EfCoreCheckpointStore Open() =>
    new(new CheckpointDbContext(new DbContextOptionsBuilder<CheckpointDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options));

string Now() => clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

async Task<List<string>> DueNow(EfCoreCheckpointStore store)
{
    var page = await store.ListThreadsAsync(new ThreadQuery { DueBy = clock.GetUtcNow() });
    // The listing is newest first, by wall-clock time; sort for stable output.
    return [.. page.Threads.Select(t => t.ThreadId).Order(StringComparer.Ordinal)];
}

// Reads the file directly, with no Strata code, to show a waiting order is only rows.
SqliteConnection Raw()
{
    var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
    connection.Open();
    return connection;
}

long Rows() => (long)Scalar("SELECT COUNT(*) FROM checkpoints")!;

object? Scalar(string sql)
{
    using var connection = Raw();
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
}

List<(string Thread, string State, string? WakeAt)> Heads()
{
    using var connection = Raw();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT thread_id, state, wake_at FROM checkpoint_threads ORDER BY thread_id";
    using var reader = command.ExecuteReader();
    var heads = new List<(string, string, string?)>();
    while (reader.Read())
    {
        heads.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
    }

    return heads;
}

void PrintHeads()
{
    Console.WriteLine("    checkpoint_threads (the head table the query port reads):");
    foreach (var (thread, state, wakeAt) in Heads())
    {
        Console.WriteLine($"      {thread}  {state,-11}  wake_at {wakeAt ?? "-"}");
    }
}

static string Describe(CheckpointWait? wait) => wait switch
{
    null => "waits for nothing",
    { Kind: WaitKind.Event } => $"waits for event '{wait.EventType}' until {wait.WakeAt}",
    { Kind: WaitKind.Timer } => $"waits for a timer until {wait.WakeAt}",
    _ => $"waits for a human until {wait.WakeAt}",
};

static string Outcome(GraphExecutionResult<Order> result) => result switch
{
    GraphExecutionResult<Order>.Completed c => $"completed, {string.Join(" / ", c.State.Log)}",
    GraphExecutionResult<Order>.Cancelled c => $"cancelled ({c.Reason}) at {c.Node}",
    GraphExecutionResult<Order>.Interrupted i => $"interrupted at {i.SuspendedNode}, {Describe(i.Wait)}",
    GraphExecutionResult<Order>.Failed f => $"failed: {f.Message}",
    _ => result.ToString(),
};

static string Money(decimal amount) => amount.ToString("0", CultureInfo.InvariantCulture);

void Step(string title)
{
    if (stepCount++ > 0)
    {
        Console.WriteLine();
    }

    Console.WriteLine($"=== {title} ===");
}

static T Expect<T>(GraphExecutionResult<Order> result, string threadId)
    where T : GraphExecutionResult<Order> =>
    result as T ?? throw new InvalidOperationException($"{threadId}: expected {typeof(T).Name}, got {Outcome(result)}");

static string Refused(GraphExecutionResult<Order> result) =>
    (result as GraphExecutionResult<Order>.Failed)?.Message
    ?? throw new InvalidOperationException($"expected a refusal, got {Outcome(result)}");

static WokenThread<Order> Single(IReadOnlyList<WokenThread<Order>> woken) =>
    woken.Count == 1 ? woken[0] : throw new InvalidOperationException($"expected one woken thread, got {woken.Count}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Order(
    string OrderId,
    decimal Total,
    bool Paid,
    string? PaymentRef,
    string? Review,
    bool Shipped,
    IReadOnlyList<string> Log)
{
    public static Order New(string orderId, decimal total) => new(orderId, total, false, null, null, false, []);

    public Order Note(string line) => this with { Log = [.. Log, line] };
}
