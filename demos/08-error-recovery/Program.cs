// Error recovery (design/04-Error-Recovery.md, design/25-Retry-Backoff.md): retry with backoff,
// fallback with the failure's details, rollback on failure, and resuming a failed thread.
//
// An order pipeline -- ValidateOrder -> ChargePayment -> SendConfirmation -- where ChargePayment
// talks to a flaky simulated payment gateway. ChargePayment is a fallible node: it signals failure
// by throwing a NodeException (transient or fatal), and its RecoveryPolicy says what the engine does
// about it: wait, retry, then route to a fallback node, or persist a Failed checkpoint if there is
// none.
//
// Every attempt of ChargePayment first reserves a receipt number on the state it is building, and
// only then calls the gateway. That reservation is the partial work the demo watches: a failed
// attempt must not leak it into the committed state, the fallback, or the checkpoint.
//
// The scenarios:
//
//   1. Transient blip -- the gateway times out twice, then works. The retries wait 200 ms, then
//      400 ms (exponential backoff, no jitter so the numbers are stable).
//   2. Rate limited   -- every call is a 503 with "Retry-After: 30s". The hint replaces the computed
//      delay, capped at the policy's 2 s max. Retries run out, and the fallback reads why from
//      NodeContext.LastFailure.
//   3. Card declined  -- a fatal error skips every retry and every wait. With no fallback, the engine
//      persists a Failed checkpoint to a real SQLite file (EfCoreCheckpointStore).
//   4. A second, independent store instance reads that checkpoint back.
//   5. The customer updates their card, and the Failed checkpoint is resumed with the new card as an
//      override: ChargePayment runs again, ValidateOrder doesn't.
//   6. A fallback that doesn't exist is caught by .Compile().
//
// The waits run on a FakeTimeProvider passed in RunOptions.TimeProvider, which the demo moves
// forward, so a 4-second backoff takes milliseconds. The delays printed come from the engine's
// RetryScheduled events, not from measuring anything.
//
// No live service and no LLM: the gateway is a deterministic stand-in, so the demo runs offline.

using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

const string Fallback = "QueueForManualReview";
const string ExpiredCard = "tok_expired_0119";
const string NewCard = "tok_visa_4242";

// bin/<config>/<tfm>/ is already gitignored, so the file needs no entry of its own. Deleted up front
// so a run never trips over a checkpoint left over from the previous one.
var dbPath = Path.Combine(AppContext.BaseDirectory, "recovery-checkpoint.db");
if (File.Exists(dbPath))
{
    File.Delete(dbPath);
}

var backoff = RecoveryPolicy.Retry(3).WithBackoff(
    initial: TimeSpan.FromMilliseconds(200), max: TimeSpan.FromSeconds(2), jitter: BackoffJitter.None);

// --- Scenarios 1-3, all against store instance #1. ---

using (var store = OpenFileBackedStore(dbPath))
{
    // --- Scenario 1: a transient blip, recovered by retries that back off. ---

    Console.WriteLine($"=== Scenario 1: transient blip -- Retry(3), backoff 200 ms doubling to 2 s, fallback '{Fallback}' ===");
    var gateway = new PaymentGateway(Outage.Blip, blipFailures: 2);
    var graph = BuildOrderGraph(gateway, new Counter(), backoff.WithFallback(Fallback));

    var (result, delays) = await RunOnFakeClock(options =>
        graph.RunAsync("order-1001", OrderState.New("ord-1001", 129.99m, NewCard), store, options));
    var completed = ExpectCompleted(result);
    Console.WriteLine($"  Completed after {gateway.Calls} gateway call(s). Waits before each retry: {Format(delays)}");
    Console.WriteLine($"  State: {completed}");
    Console.WriteLine($"  Only the successful attempt's reservation was committed: {completed.ReceiptId}");
    Console.WriteLine(
        $"  Checkpoint for 'order-1001': {CheckpointStatus(store, "order-1001")} (a recovered failure leaves nothing behind)");
    Check(gateway.Calls == 3 && completed.ReceiptId == "RCPT-ord-1001-3", "expected the third attempt to succeed");
    Check(delays.SequenceEqual([Ms(200), Ms(400)]), "expected 200 ms, then 400 ms");

    // --- Scenario 2: rate limited until retries run out; the fallback learns why. ---

    Console.WriteLine();
    Console.WriteLine($"=== Scenario 2: rate limited (Retry-After: 30s) -- Retry(2), same backoff, fallback '{Fallback}' ===");
    gateway = new PaymentGateway(Outage.RateLimited);
    graph = BuildOrderGraph(gateway, new Counter(), (backoff with { MaxRetries = 2 }).WithFallback(Fallback));

    (result, delays) = await RunOnFakeClock(options =>
        graph.RunAsync("order-1002", OrderState.New("ord-1002", 54.00m, NewCard), store, options));
    completed = ExpectCompleted(result);
    Console.WriteLine($"  Retries exhausted after {gateway.Calls} gateway call(s). Waits: {Format(delays)}");
    Console.WriteLine("  The 30 s hint replaced the computed delay, capped at the policy's 2 s max.");
    Console.WriteLine($"  State: {completed}");
    Console.WriteLine(
        $"  ReceiptId is null and 'SendConfirmation' never ran (Confirmed: {completed.Confirmed}): the fallback ran on the state from before ChargePayment.");
    Check(delays.SequenceEqual([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)]), "expected the hint, capped at 2 s");
    Check(
        completed is { PaymentStatus: "manual_review", ReceiptId: null, Confirmed: false },
        "expected the manual-review fallback with the failed attempts' reservations rolled back");
    Check(
        completed.ReviewNote == "ChargePayment failed (Transient) after 3 attempt(s): gateway unavailable (503); gateway asked to wait 30s",
        "expected the fallback to record the failure it was given");

    // --- Scenario 3: a fatal error with no fallback. ---

    Console.WriteLine();
    Console.WriteLine("=== Scenario 3: card declined -- Retry(3), same backoff, no fallback ===");
    gateway = new PaymentGateway(Outage.None);
    graph = BuildOrderGraph(gateway, new Counter(), backoff);

    (result, delays) = await RunOnFakeClock(options =>
        graph.RunAsync("order-1003", OrderState.New("ord-1003", 899.00m, ExpiredCard), store, options));
    if (result is not GraphExecutionResult<OrderState>.NodeFailed failed || failed.Attempts != 1)
    {
        throw new InvalidOperationException($"expected NodeFailed after 1 attempt, got: {result}");
    }

    Console.WriteLine(
        $"  Failed: node '{failed.Node}' gave up after {failed.Attempts} attempt(s) -- '{failed.Message}'. Retries scheduled: {delays.Count}.");
    Console.WriteLine("  A fatal error is never retried, so it never waits either.");
    Check(delays.Count == 0, "expected no retry for a fatal error");
}

// --- Step 4: store instance #1 is disposed. A brand-new one reads the Failed checkpoint from disk. ---

Console.WriteLine();
Console.WriteLine("=== Step 4: reading the Failed checkpoint back (store instance #2) ===");
using var store2 = OpenFileBackedStore(dbPath);
var checkpoint = await store2.LoadAsync("order-1003")
    ?? throw new InvalidOperationException("a Failed checkpoint must have been persisted");

Console.WriteLine($"  status:         {checkpoint.Status}");
Console.WriteLine($"  failed node:    {checkpoint.SuspendedNode}");
Console.WriteLine($"  resume from:    {checkpoint.NextNode}");
Console.WriteLine($"  frozen state:   {checkpoint.FrozenState?.ToJsonString()}");
foreach (var (key, value) in (checkpoint.Metadata ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"  metadata.{key}: {value}");
}

Console.WriteLine(
    "  The frozen state is the last committed state (after ValidateOrder). ReceiptId is null: the declined attempt's reservation never reached it.");
Check(checkpoint.Status == Datalium.Strata.Interruption.CheckpointStatus.Failed, "expected a Failed checkpoint");
Check(checkpoint.FrozenState!["ReceiptId"] is null, "expected no receipt in the committed state");

// --- Step 5: fix the cause, then resume the Failed checkpoint. ---

Console.WriteLine();
Console.WriteLine("=== Step 5: the customer updates their card; the Failed checkpoint is resumed (store instance #2) ===");
var resumeGateway = new PaymentGateway(Outage.None);
var validations = new Counter();
var resumeGraph = BuildOrderGraph(resumeGateway, validations, backoff);

// Resuming a failed thread re-enters the node that failed, with the committed state plus the
// signal's overrides. It isn't answering an interrupt boundary, so no decision is recorded; the
// signal is just the carrier for the fix and who made it.
var fix = HumanSignal.Approve("support@shop.example").WithOverride("CardToken", NewCard);
var resumed = ExpectCompleted(await resumeGraph.ResumeAsync("order-1003", fix, store2));
Console.WriteLine($"  State: {resumed}");
Console.WriteLine(
    $"  ChargePayment ran again ({resumeGateway.Calls} gateway call), ValidateOrder did not ({validations.Count} runs), and SendConfirmation completed the order.");
Check(resumeGateway.Calls == 1 && validations.Count == 0, "expected the resume to re-enter ChargePayment only");
Check(resumed is { PaymentStatus: "charged", Confirmed: true, CardToken: NewCard }, "expected the order to complete on the new card");

var again = await resumeGraph.ResumeAsync("order-1003", fix, store2);
if (again is not GraphExecutionResult<OrderState>.Failed refused)
{
    throw new InvalidOperationException($"expected the second resume to be refused, got: {again}");
}

Console.WriteLine($"  Resuming the same checkpoint again is refused: {refused.Message}");
Check(resumeGateway.Calls == 1, "a refused resume must not charge the card again");

// --- Step 6: a fallback that doesn't exist. ---

Console.WriteLine();
Console.WriteLine("=== Step 6: a recovery policy is checked at .Compile(), not at runtime ===");
try
{
    new StateGraph<OrderState>()
        .AddFallibleNode("ChargePayment", state => state)
        .SetRecoveryPolicy(
            "ChargePayment", RecoveryPolicy.Retry(1).WithFallback("QueueForManualReveiw"))
        .Compile();
    throw new InvalidOperationException("expected the misspelled fallback to be rejected");
}
catch (InvalidOperationException ex) when (ex.Message.Contains("does not exist"))
{
    Console.WriteLine($"  Rejected: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine(
    "=== Done: retries backed off, a rate limit's hint set the wait, the fallback knew why, and a fatal failure was resumed once its cause was fixed. ===");

// ValidateOrder -> ChargePayment -> SendConfirmation. When `policy` names a fallback node,
// QueueForManualReview is added as that node. It gets no ordinary edge, so the recovery policy is
// the only way in.
static CompiledGraph<OrderState> BuildOrderGraph(PaymentGateway gateway, Counter validations, RecoveryPolicy policy)
{
    var graph = new StateGraph<OrderState>()
        .AddNode("ValidateOrder", state =>
        {
            validations.Count++;
            return state with { PaymentStatus = "validated" };
        })
        .AddFallibleNode("ChargePayment", state =>
        {
            // Reserve a receipt number on the state being built *before* calling the gateway. If the
            // call throws, the reservation is discarded with the rest of the failed attempt.
            var next = state with { ReceiptId = $"RCPT-{state.OrderId}-{gateway.Calls + 1}" };

            gateway.Charge(state.OrderId, state.Amount, state.CardToken);

            return next with { PaymentStatus = "charged" };
        })
        .AddNode("SendConfirmation", state => state with { Confirmed = true })
        .AddEdge("ValidateOrder", "ChargePayment")
        .AddEdge("ChargePayment", "SendConfirmation")
        .SetRecoveryPolicy("ChargePayment", policy);

    if (policy.Fallback is not null)
    {
        graph.AddNode(policy.Fallback, (state, ctx, _) =>
        {
            // Only a fallback's context carries LastFailure: the final failure that made the policy
            // give up, with its kind, attempts and retry hint.
            var failure = ctx.LastFailure ?? throw new InvalidOperationException("a fallback always has a LastFailure");
            var note = $"{failure.Node} failed ({failure.Kind}) after {failure.Attempts} attempt(s): {failure.Message}";
            if (failure.RetryAfter is { } wait)
            {
                note += $"; gateway asked to wait {wait.TotalSeconds:0}s";
            }

            return ValueTask.FromResult(state with { PaymentStatus = "manual_review", ReviewNote = note });
        });
    }

    return graph.Compile();
}

// Runs `run` on a fake clock, moving it forward until the run ends, and returns the result with the
// delay of every retry the engine scheduled. The step size only decides how fast the fake time
// passes; the delays come from the engine's events.
static async Task<(GraphExecutionResult<OrderState> Result, List<TimeSpan> Delays)> RunOnFakeClock(
    Func<RunOptions, Task<GraphExecutionResult<OrderState>>> run)
{
    var clock = new FakeTimeProvider();
    var events = new RunEventChannel();
    var task = run(new RunOptions { TimeProvider = clock, Observer = events });
    while (!task.IsCompleted)
    {
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(1);
    }

    var result = await task;
    var delays = new List<TimeSpan>();
    await foreach (var runEvent in events.Reader.ReadAllAsync())
    {
        if (runEvent is RetryScheduled retry)
        {
            delays.Add(retry.Delay);
        }
    }

    return (result, delays);
}

static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

static string Format(List<TimeSpan> delays) =>
    delays.Count == 0 ? "none" : string.Join(", ", delays.Select(delay => $"{delay.TotalMilliseconds:0} ms"));

static OrderState ExpectCompleted(GraphExecutionResult<OrderState> result) =>
    (result as GraphExecutionResult<OrderState>.Completed)?.State
    ?? throw new InvalidOperationException($"expected Completed, got: {result}");

static string CheckpointStatus(ICheckpointStore store, string threadId) =>
    store.Load(threadId)?.Status.ToString() ?? "none";

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static EfCoreCheckpointStore OpenFileBackedStore(string dbPath)
{
    var options = new DbContextOptionsBuilder<CheckpointDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options;
    return new EfCoreCheckpointStore(new CheckpointDbContext(options));
}

sealed record OrderState(
    string OrderId,
    decimal Amount,
    string CardToken,
    string PaymentStatus,
    string? ReceiptId,
    bool Confirmed,
    string? ReviewNote)
{
    public static OrderState New(string orderId, decimal amount, string cardToken) =>
        new(orderId, amount, cardToken, PaymentStatus: "new", ReceiptId: null, Confirmed: false, ReviewNote: null);
}

sealed class Counter
{
    public int Count { get; set; }
}

/// <summary>How the simulated gateway misbehaves. Whatever the mode, it declines an expired card.</summary>
enum Outage
{
    None,

    /// <summary>Times out for the first <c>blipFailures</c> calls, then works.</summary>
    Blip,

    /// <summary>Every call is a 503 that asks the caller to wait 30 seconds.</summary>
    RateLimited,
}

sealed class PaymentGateway(Outage outage, int blipFailures = 0)
{
    public int Calls { get; private set; }

    public void Charge(string orderId, decimal amount, string cardToken)
    {
        Calls++;
        NodeException? error = outage switch
        {
            Outage.Blip when Calls <= blipFailures => NodeException.Transient("gateway timed out"),
            // A 503 with Retry-After: the hint travels with the exception.
            Outage.RateLimited => NodeException.Transient("gateway unavailable (503)", TimeSpan.FromSeconds(30)),
            _ when cardToken.StartsWith("tok_expired", StringComparison.Ordinal) => NodeException.Fatal("card declined: expired"),
            _ => null,
        };

        var outcome = error is null
            ? "approved"
            : $"{(error.Kind == NodeFailureKind.Transient ? "transient" : "fatal")} error: {error.Message}";
        Console.WriteLine($"    [gateway] call {Calls} for {orderId} (${amount:F2}, {cardToken}): {outcome}");

        if (error is not null)
        {
            throw error;
        }
    }
}
