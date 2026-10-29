// Async execution, node context and cancellation (design/17-Async-Execution.md).
//
// A vendor-payment run: LoadInvoice -> Screen (a parallel group: Sanctions, Duplicates) ->
// ScoreFraud -> PostToLedger -> PayVendor. Most handlers are async and take a NodeContext and a
// CancellationToken; PostToLedger is a sync call into a legacy ledger SDK that can't see a token.
//
//   1. A healthy run: what NodeContext tells each handler (thread, qualified path, attempt, run id,
//      run metadata), with run-level inputs (metadata, services, run id, clock) in RunOptions.
//   2. The fraud API hangs: SetTimeout turns the stuck attempt into a transient failure, and
//      Retry(1) recovers on attempt 2. Without the retry, the timeout fails the run.
//   3. The ledger SDK blocks: a sync handler is abandoned, not waited for. The run finishes while
//      it is still blocked -- and when it finally returns, its posting lands too.
//   4. An operator cancels during the bank call: Cancelled, a resumable `cancelled` checkpoint,
//      no retry, and a resume that re-enters PayVendor only.
//   5. The run's deadline passes during the bank call: the same, with reason Deadline.
//
// The clock is a FakeTimeProvider passed as RunOptions.TimeProvider. Node timeouts and the deadline
// run on it, so the demo fires them exactly when it means to (after the handler has started) and
// never waits for real time. The services are deterministic stand-ins; nothing goes over a network.

using System.Collections.Concurrent;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Microsoft.Extensions.Time.Testing;

var patience = TimeSpan.FromSeconds(10);
var store = new InMemoryCheckpointStore();

// --- Scenario 1: a healthy run, and what each handler's NodeContext says. ---

Console.WriteLine("=== Scenario 1: a healthy payment run -- async handlers and their NodeContext ===");
var fakes = new Fakes();
var graph = PaymentRun.Build(fakes);
var result = await graph.RunAsync("inv-1001", Invoice.New("inv-1001", "Globex GmbH", 1250.00m, "USD"), store, new RunOptions
{
    Metadata = { ["tenant"] = "acme-eu" },
    RunId = "payrun-2026-10-24",
    Services = fakes.Services,
    TimeProvider = fakes.Clock,
});
var paid = ExpectCompleted(result);
Console.WriteLine("  RunOptions: Metadata { tenant = acme-eu }, RunId = payrun-2026-10-24, Services = { IFxRates }");
Console.WriteLine("  What each async handler was told:");
Console.WriteLine($"    {"node path",-20} {"node",-12} attempt  {"thread",-9} {"run id",-18} tenant");
string[] order = ["LoadInvoice", "Screen/Sanctions", "Screen/Duplicates", "ScoreFraud", "PayVendor"];
foreach (var path in order)
{
    var seen = fakes.Contexts.Single(c => c.Path == path);
    Console.WriteLine($"    {seen.Path,-20} {seen.Node,-12} {seen.Attempt,-8} {seen.Thread,-9} {seen.RunId,-18} {seen.Tenant}");
}

Console.WriteLine("  PostToLedger is a sync handler (Func<Invoice, Invoice>): it gets no context and no token.");
Console.WriteLine($"  Completed: {paid.Summary()}");
Check(fakes.Contexts.Count == order.Length, "expected exactly one context per async handler");
Check(
    fakes.Contexts.All(c => c is { Attempt: 1, Thread: "inv-1001", RunId: "payrun-2026-10-24", Tenant: "acme-eu" }),
    "expected every handler to see attempt 1, the thread, the run id and the tenant");
Check(
    fakes.Contexts.Single(c => c.Path == "Screen/Sanctions").Node == "Sanctions",
    "expected a branch's Node to be its own name and its NodePath to be qualified by the group");
Check(paid.Checks.SequenceEqual(["sanctions: clear", "duplicates: none"]), "expected the checks in declaration order");
Check(paid.AmountEur == 1150.00m, "expected the FX service from RunOptions.Services to convert the amount");

// --- Scenario 2: the fraud API hangs. A timeout is a transient failure; a retry recovers. ---

Console.WriteLine();
Console.WriteLine($"=== Scenario 2: the fraud API hangs -- SetTimeout({PaymentRun.FraudTimeout.TotalSeconds:0} s) + Retry(1) ===");
fakes = new Fakes { Fraud = { HangOnAttempt = 1 } };
graph = PaymentRun.Build(fakes);
var run = graph.RunAsync("inv-1002", Invoice.New("inv-1002", "Initech", 480.00m, "EUR"), store, Options(fakes));
await fakes.Fraud.Stall.Started.Task.WaitAsync(patience);
fakes.Clock.Advance(PaymentRun.FraudTimeout);
paid = ExpectCompleted(await run.WaitAsync(patience));
await fakes.Fraud.Stall.SawCancel.Task.WaitAsync(patience);
var fraudAttempts = fakes.Contexts.Where(c => c.Node == "ScoreFraud").Select(c => c.Attempt).ToList();
Console.WriteLine($"  Attempt 1 started, then the clock moved {PaymentRun.FraudTimeout.TotalSeconds:0} s: its token was cancelled and the attempt failed as transient.");
Console.WriteLine($"  ScoreFraud attempts seen in NodeContext.Attempt: [{string.Join(", ", fraudAttempts)}] -- each retry gets a fresh timeout.");
Console.WriteLine($"  Completed: {paid.Summary()}");
Check(fraudAttempts.SequenceEqual([1, 2]), "expected the timed-out attempt to be retried once");

Console.WriteLine("  The same hang with no retry:");
fakes = new Fakes { Fraud = { HangOnAttempt = 1 } };
graph = PaymentRun.Build(fakes, fraudRetries: 0);
run = graph.RunAsync("inv-1002b", Invoice.New("inv-1002b", "Initech", 480.00m, "EUR"), store, Options(fakes));
await fakes.Fraud.Stall.Started.Task.WaitAsync(patience);
fakes.Clock.Advance(PaymentRun.FraudTimeout);
if (await run.WaitAsync(patience) is not GraphExecutionResult<Invoice>.NodeFailed failed)
{
    throw new InvalidOperationException("expected the exhausted timeout to fail the run");
}

var failedCheckpoint = store.Load("inv-1002b")!;
Console.WriteLine($"    NodeFailed: '{failed.Node}' after {failed.Attempts} attempt(s): {failed.Message}");
Console.WriteLine($"    Checkpoint: {failedCheckpoint.Status}, error_kind = {failedCheckpoint.Metadata!["error_kind"]}");
Check(failed.Message == "node 'ScoreFraud' timed out after 2000 ms", "expected the engine's timeout message");
Check(failedCheckpoint.Metadata["error_kind"] == "transient", "expected a timeout to be a transient failure");

// --- Scenario 3: a sync handler that blocks is abandoned, not waited for. ---

Console.WriteLine();
Console.WriteLine($"=== Scenario 3: the legacy ledger SDK blocks -- a sync handler is abandoned, SetTimeout({PaymentRun.LedgerTimeout.TotalSeconds:0} s) + Retry(1) ===");
fakes = new Fakes { Ledger = { BlockFirstCall = true } };
graph = PaymentRun.Build(fakes);
run = graph.RunAsync("inv-1003", Invoice.New("inv-1003", "Umbrella Ltd", 99.90m, "EUR"), store, Options(fakes));
await fakes.Ledger.FirstCallStarted.Task.WaitAsync(patience);
fakes.Clock.Advance(PaymentRun.LedgerTimeout);
paid = ExpectCompleted(await run.WaitAsync(patience));
var blockedWhenRunEnded = !fakes.Ledger.FirstCallFinished.Task.IsCompleted;
var entriesWhenRunEnded = fakes.Ledger.Entries.ToList();
Console.WriteLine($"  The run completed while ledger call 1 was still blocked: {blockedWhenRunEnded}");
Console.WriteLine($"  Ledger calls: {fakes.Ledger.Calls}; entries when the run ended: [{string.Join(", ", entriesWhenRunEnded)}]");
Console.WriteLine($"  Completed: {paid.Summary()}");
fakes.Ledger.Gate.Set();
await fakes.Ledger.FirstCallFinished.Task.WaitAsync(patience);
var entries = fakes.Ledger.Entries.ToList();
Console.WriteLine("  Now the SDK's first call returns. Its result is discarded, but its side effect is not:");
Console.WriteLine($"    ledger entries: [{string.Join(", ", entries)}] -- one invoice, posted twice.");
Console.WriteLine("  A handler whose side effects must not overlap a retry should be async and honour its token.");
Check(blockedWhenRunEnded, "expected the engine not to wait for the abandoned sync handler");
Check(entriesWhenRunEnded.SequenceEqual(["LED-1002 inv-1003"]), "expected only the retry's posting when the run ended");
Check(paid.LedgerRef == "LED-1002", "expected the state to hold the retry's ledger reference");
Check(entries.SequenceEqual(["LED-1002 inv-1003", "LED-1001 inv-1003"]), "expected the abandoned call to post late");

// --- Scenario 4: an operator cancels the run mid-flight; resume it. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: an operator cancels during the bank call -- Cancelled, then resumed ===");
fakes = new Fakes { Bank = { Hang = true } };
graph = PaymentRun.Build(fakes);
using (var cts = new CancellationTokenSource())
{
    run = graph.RunAsync("inv-1004", Invoice.New("inv-1004", "Hooli", 3200.00m, "USD"), store, Options(fakes), cts.Token);
    await fakes.Bank.Stall.Started.Task.WaitAsync(patience);
    await cts.CancelAsync();
    var cancelled = ExpectCancelled(await run.WaitAsync(patience));
    await fakes.Bank.Stall.SawCancel.Task.WaitAsync(patience);
    Console.WriteLine($"  Cancelled at '{cancelled.Node}', reason {cancelled.Reason}, stopped at [{string.Join(", ", cancelled.StoppedAt)}] (RunAsync returned it; it didn't throw).");
    Console.WriteLine($"  The bank client saw its token cancelled. Bank calls: {fakes.Bank.Calls} -- PayVendor's Retry(2) never ran: a cancellation isn't a failure.");
    var checkpoint = store.Load("inv-1004")!;
    PrintCheckpoint(checkpoint);
    Check(cancelled is { Node: "PayVendor", Reason: CancelReason.Requested }, "expected a requested cancellation at PayVendor");
    Check(fakes.Bank.Calls == 1, "expected no retry after a cancellation");
    Check(checkpoint.Status == CheckpointStatus.Cancelled, "expected a cancelled checkpoint");
    Check(checkpoint.SchemaVersion == CheckpointSchema.CancelledVersion, "expected schema 1.2.0");
    Check(checkpoint is { SuspendedNode: "PayVendor", NextNode: "PayVendor" }, "expected the checkpoint at PayVendor");
    Check(checkpoint.Metadata!["cancel_reason"] == "requested", "expected cancel_reason requested");
    Check(checkpoint.FrozenState!["PaymentRef"] is null, "expected the state PayVendor started with");
}

fakes.Bank.Hang = false;
var contextsBefore = fakes.Contexts.Count;
// No Metadata here: a resume carries the stored run metadata forward.
var resumed = await graph.ResumeAsync(
    "inv-1004", HumanSignal.Approve("ops-oncall"), store, new RunOptions { Services = fakes.Services, TimeProvider = fakes.Clock });
paid = ExpectCompleted(resumed);
var resumedContexts = fakes.Contexts.Skip(contextsBefore).ToList();
Console.WriteLine($"  Resumed: only [{string.Join(", ", resumedContexts.Select(c => $"{c.Path} attempt {c.Attempt}, tenant {c.Tenant}"))}] ran.");
Console.WriteLine($"  Invoice loads: {fakes.Loads}, ledger calls: {fakes.Ledger.Calls}, bank calls: {fakes.Bank.Calls}.");
Console.WriteLine($"  Completed: {paid.Summary()}");
var again = await graph.ResumeAsync("inv-1004", HumanSignal.Approve("ops-oncall"), store);
Console.WriteLine($"  A second resume of the same checkpoint: {((GraphExecutionResult<Invoice>.Failed)again).Message}");
Check(resumedContexts is [{ Path: "PayVendor", Attempt: 1, Tenant: "acme-eu" }], "expected the resume to re-enter PayVendor only, at attempt 1");
Check((fakes.Loads, fakes.Ledger.Calls, fakes.Bank.Calls) == (1, 1, 2), "expected no earlier node to run again");
Check(again is GraphExecutionResult<Invoice>.Failed f2 && f2.Message.Contains("already resumed"), "expected a second resume to be refused");

// --- Scenario 5: the run's deadline passes mid-flight; resume it. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 5: the run's deadline passes during the bank call -- RunOptions.Deadline ===");
fakes = new Fakes { Bank = { Hang = true } };
graph = PaymentRun.Build(fakes);
var deadline = fakes.Clock.GetUtcNow() + TimeSpan.FromSeconds(30);
run = graph.RunAsync("inv-1005", Invoice.New("inv-1005", "Stark Industries", 7400.00m, "USD"), store, Options(fakes, deadline));
await fakes.Bank.Stall.Started.Task.WaitAsync(patience);
fakes.Clock.Advance(TimeSpan.FromSeconds(30));
var late = ExpectCancelled(await run.WaitAsync(patience));
var lateCheckpoint = store.Load("inv-1005")!;
Console.WriteLine($"  Cancelled at '{late.Node}', reason {late.Reason}; checkpoint {lateCheckpoint.Status}, cancel_reason = {lateCheckpoint.Metadata!["cancel_reason"]}");
fakes.Bank.Hang = false;
paid = ExpectCompleted(await graph.ResumeAsync(
    "inv-1005", HumanSignal.Approve("ops-oncall"), store, Options(fakes, fakes.Clock.GetUtcNow() + TimeSpan.FromSeconds(30))));
Console.WriteLine($"  Resumed with a fresh deadline: {paid.Summary()}");
Check(late is { Node: "PayVendor", Reason: CancelReason.Deadline }, "expected a deadline cancellation at PayVendor");
Check(lateCheckpoint.Metadata["cancel_reason"] == "deadline", "expected cancel_reason deadline");
Check((fakes.Loads, fakes.Bank.Calls) == (1, 2), "expected the resume to re-enter PayVendor only");

Console.WriteLine();
Console.WriteLine("=== Done: async handlers saw their context, timeouts became retries, a blocked handler was abandoned, and cancelled runs resumed where they stopped. ===");

static RunOptions Options(Fakes fakes, DateTimeOffset? deadline = null) => new()
{
    Metadata = { ["tenant"] = "acme-eu" },
    Services = fakes.Services,
    TimeProvider = fakes.Clock,
    Deadline = deadline,
};

static void PrintCheckpoint(Checkpoint checkpoint)
{
    var metadata = string.Join(", ", checkpoint.Metadata!.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} = {m.Value}"));
    Console.WriteLine($"  Checkpoint: {checkpoint.Status}, schema {checkpoint.SchemaVersion}, next node {checkpoint.NextNode}, metadata {{ {metadata} }}");
    Console.WriteLine($"    frozen state: LedgerRef = {checkpoint.FrozenState!["LedgerRef"]}, PaymentRef = {checkpoint.FrozenState["PaymentRef"]?.ToString() ?? "null"} (the state PayVendor started with)");
}

static Invoice ExpectCompleted(GraphExecutionResult<Invoice> result) =>
    result is GraphExecutionResult<Invoice>.Completed completed
        ? completed.State
        : throw new InvalidOperationException($"expected Completed, got: {result}");

static GraphExecutionResult<Invoice>.Cancelled ExpectCancelled(GraphExecutionResult<Invoice> result) =>
    result as GraphExecutionResult<Invoice>.Cancelled
        ?? throw new InvalidOperationException($"expected Cancelled, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"check failed: {message}");
    }
}

sealed record Invoice(
    string Id,
    string Vendor,
    decimal Amount,
    string Currency,
    decimal? AmountEur,
    IReadOnlyList<string> Checks,
    int? FraudScore,
    string? LedgerRef,
    string? PaymentRef)
{
    public static Invoice New(string id, string vendor, decimal amount, string currency) =>
        new(id, vendor, amount, currency, null, [], null, null, null);

    public string Summary() =>
        $"{Vendor}: {Currency} {Amount:0.00}{(Currency == "EUR" ? "" : $" = EUR {AmountEur:0.00}")}, fraud score {FraudScore}, ledger {LedgerRef}, payment {PaymentRef}";
}

static class PaymentRun
{
    public static readonly TimeSpan FraudTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan LedgerTimeout = TimeSpan.FromSeconds(5);

    public static CompiledGraph<Invoice> Build(Fakes f, int fraudRetries = 1)
    {
        var screen = new ParallelBranches<Invoice>()
            .Branch("Sanctions", async (invoice, ctx, ct) =>
            {
                f.Saw(ctx);
                await Task.Yield();
                return invoice with { Checks = ["sanctions: clear"] };
            })
            .Branch("Duplicates", async (invoice, ctx, ct) =>
            {
                f.Saw(ctx);
                await Task.Yield();
                return invoice with { Checks = ["duplicates: none"] };
            });

        return new StateGraph<Invoice>()
            .AddNode("LoadInvoice", async (invoice, ctx, ct) =>
            {
                f.Saw(ctx);
                Interlocked.Increment(ref f.Loads);
                var rates = (IFxRates)ctx.Services!.GetService(typeof(IFxRates))!;
                var rate = await rates.ToEurAsync(invoice.Currency, ct);
                return invoice with { AmountEur = Math.Round(invoice.Amount * rate, 2) };
            })
            .AddParallelGroup("Screen", screen, (entry, outputs) => entry with { Checks = [.. outputs.SelectMany(o => o.Checks)] })
            .AddFallibleNode("ScoreFraud", async (invoice, ctx, ct) =>
            {
                f.Saw(ctx);
                return invoice with { FraudScore = await f.Fraud.ScoreAsync(ctx.Attempt, ct) };
            })
            .SetTimeout("ScoreFraud", FraudTimeout)
            .SetRecoveryPolicy("ScoreFraud", fraudRetries == 0 ? RecoveryPolicy.Default : RecoveryPolicy.Retry(fraudRetries))
            // A sync handler: the legacy SDK has no async API and no way to be told to stop.
            .AddFallibleNode("PostToLedger", invoice => invoice with { LedgerRef = f.Ledger.Post(invoice.Id) })
            .SetTimeout("PostToLedger", LedgerTimeout)
            .SetRecoveryPolicy("PostToLedger", RecoveryPolicy.Retry(1))
            .AddFallibleNode("PayVendor", async (invoice, ctx, ct) =>
            {
                f.Saw(ctx);
                return invoice with { PaymentRef = await f.Bank.PayAsync(ct) };
            })
            .SetRecoveryPolicy("PayVendor", RecoveryPolicy.Retry(2))
            .AddEdge("LoadInvoice", "Screen")
            .AddEdge("Screen", "ScoreFraud")
            .AddEdge("ScoreFraud", "PostToLedger")
            .AddEdge("PostToLedger", "PayVendor")
            .Compile();
    }
}

/// <summary>What one async handler was told by its NodeContext.</summary>
sealed record Seen(string Node, string Path, int Attempt, string Thread, string RunId, string? Tenant);

/// <summary>The stand-in services one scenario runs against, and what they recorded.</summary>
sealed class Fakes
{
    public int Loads;

    public FakeTimeProvider Clock { get; } = new();

    public ConcurrentQueue<Seen> Contexts { get; } = new();

    public FraudApi Fraud { get; } = new();

    public LedgerSdk Ledger { get; } = new();

    public BankClient Bank { get; } = new();

    public IServiceProvider Services { get; } = new SingleService(typeof(IFxRates), new FxRates());

    public void Saw(NodeContext ctx) =>
        Contexts.Enqueue(new Seen(ctx.Node, ctx.NodePath, ctx.Attempt, ctx.ThreadId, ctx.RunId, ctx.Metadata.GetValueOrDefault("tenant")));
}

/// <summary>
/// A call that hangs until its token is cancelled. Says when it started and whether it saw the
/// cancellation, so a scenario can act at the right moment and check the token arrived.
/// </summary>
sealed class Stall
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource SawCancel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task UntilCancelledAsync(CancellationToken ct)
    {
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            SawCancel.TrySetResult();
            throw;
        }
    }
}

sealed class FraudApi
{
    /// <summary>The attempt that hangs; 0 for none.</summary>
    public int HangOnAttempt { get; set; }

    public Stall Stall { get; } = new();

    public async Task<int> ScoreAsync(int attempt, CancellationToken ct)
    {
        if (attempt == HangOnAttempt)
        {
            await Stall.UntilCancelledAsync(ct);
        }

        await Task.Yield();
        return 12;
    }
}

sealed class BankClient
{
    private int _calls;

    public bool Hang { get; set; }

    public Stall Stall { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public async Task<string> PayAsync(CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _calls);
        if (Hang)
        {
            await Stall.UntilCancelledAsync(ct);
        }

        await Task.Yield();
        return $"PAY-{7000 + call}";
    }
}

/// <summary>A blocking SDK: the first call can be made to block on a gate the scenario controls.</summary>
sealed class LedgerSdk
{
    private int _calls;

    public bool BlockFirstCall { get; set; }

    public ManualResetEventSlim Gate { get; } = new();

    public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource FirstCallFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> Entries { get; } = new();

    public int Calls => Volatile.Read(ref _calls);

    public string Post(string invoiceId)
    {
        var call = Interlocked.Increment(ref _calls);
        var blocks = call == 1 && BlockFirstCall;
        if (blocks)
        {
            FirstCallStarted.TrySetResult();
            Gate.Wait(TimeSpan.FromSeconds(30));
        }

        var reference = $"LED-{1000 + call}";
        Entries.Enqueue($"{reference} {invoiceId}");
        if (blocks)
        {
            FirstCallFinished.TrySetResult();
        }

        return reference;
    }
}

interface IFxRates
{
    ValueTask<decimal> ToEurAsync(string currency, CancellationToken ct);
}

sealed class FxRates : IFxRates
{
    public ValueTask<decimal> ToEurAsync(string currency, CancellationToken ct) =>
        ValueTask.FromResult(currency == "USD" ? 0.92m : 1.00m);
}

sealed class SingleService(Type type, object service) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType == type ? service : null;
}
