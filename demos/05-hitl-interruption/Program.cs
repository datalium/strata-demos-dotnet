// Human-in-the-loop interruption (STRATA-SPEC-002) with typed human signals (design/21).
//
// PrepareWireTransfer -> [interrupt boundary] -> ExecuteWireTransfer, rejecting to
// CancelWireTransfer. A node drafts something high-risk (an outbound wire transfer), the graph
// freezes for a human before anything irreversible happens, and the human's answer decides where it
// goes next.
//
// The core proof is still "Flush": the checkpoints go to a real SQLite file (EfCoreCheckpointStore).
// Store instance #1 runs two transfers to their boundary and is disposed. Store instance #2, which
// shares nothing with #1 but the file, stands in for the process after a restart: it finds the
// waiting transfers with the query port and answers them.
//
// What the answer carries, in 0.9:
//   - Typed overrides. HumanSignal.Approve(reviewer).WithOverride("Amount", 4150.25m) writes a JSON
//     number, so it fits a decimal field. The old string-only FieldOverrides would write "4150.25",
//     which doesn't fit, and the engine refuses it before touching the checkpoint.
//   - A recorded decision. The resume records who answered as a HumanDecision, and the nodes after
//     the boundary read it from NodeContext.Decision. The audit trail comes from the engine, not from
//     a field the reviewer has to fill in.
//   - A reject route. AddInterruptBoundary(name, onReject: ...) sends a rejection to its own node,
//     so the transfer node never sees one.

using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage.Memory;
using Microsoft.EntityFrameworkCore;

// bin/<config>/<tfm>/ is already gitignored, so the file needs no entry of its own. Deleted up front
// so a run never resumes a checkpoint left over from the previous one.
var dbPath = Path.Combine(AppContext.BaseDirectory, "checkpoint.db");
if (File.Exists(dbPath))
{
    File.Delete(dbPath);
}

const string AcmeThread = "wire-acme-corp";
const string GlobexThread = "wire-globex";

var bank = new Bank();
var decisions = new List<HumanDecision>();
var graph = new StateGraph<WireTransferState>()
    .AddNode("PrepareWireTransfer", state => state with { InvoiceGenerated = true, Status = "awaiting approval" })
    .AddInterruptBoundary("PrepareWireTransfer", onReject: "CancelWireTransfer")
    .AddNode("ExecuteWireTransfer", (state, ctx, _) =>
    {
        // The run resumed at the boundary, so a decision is in effect. The reviewer comes from it,
        // and state.Amount already holds any amount the reviewer corrected.
        var decision = ctx.Decision ?? throw new InvalidOperationException("a transfer must not run without a decision");
        decisions.Add(decision);
        var reference = bank.Send(state.Beneficiary, state.Amount, state.Currency);
        return ValueTask.FromResult(state with
        {
            Status = $"sent ({reference})",
            ApprovedBy = decision.Reviewer,
        });
    })
    .AddNode("CancelWireTransfer", (state, ctx, _) =>
    {
        // A rejection is recorded as a decision too, so the reject route knows who said no.
        var decision = ctx.Decision!;
        decisions.Add(decision);
        return ValueTask.FromResult(state with { Status = $"cancelled by {decision.Reviewer}" });
    })
    .AddEdge("PrepareWireTransfer", "ExecuteWireTransfer")
    .Compile();

// --- Step 1: run two transfers to the boundary, using store instance #1. ---

Console.WriteLine("=== Step 1: two transfers run to the interrupt boundary (store instance #1) ===");
using (var store = OpenFileBackedStore(dbPath))
{
    foreach (var (threadId, transfer) in new[]
    {
        (AcmeThread, WireTransferState.New("Acme Corp", 4200.50m)),
        (GlobexThread, WireTransferState.New("Globex Ltd", 9800.00m)),
    })
    {
        var result = await graph.RunAsync(threadId, transfer, store);
        var interrupted = result as GraphExecutionResult<WireTransferState>.Interrupted
            ?? throw new InvalidOperationException($"expected '{threadId}' to interrupt at the boundary, got: {result}");
        Console.WriteLine($"  {interrupted.ThreadId}: suspended at {interrupted.SuspendedNode}, next {interrupted.NextNode}");
        Console.WriteLine($"    frozen: {interrupted.FrozenState}");
    }
}

Console.WriteLine("  Store instance #1 disposed. The checkpoints now exist only in the SQLite file.");
Check(bank.Sent.Count == 0, "nothing may be sent before a human answers");

// --- Step 2: "restart". A new store instance finds what is waiting. ---

Console.WriteLine();
Console.WriteLine("=== Step 2: after the restart, store instance #2 lists the transfers waiting for a human ===");
Console.WriteLine($"  SQLite file: {dbPath}");
using var restarted = OpenFileBackedStore(dbPath);
var waiting = await ListWaitingAsync(restarted);
foreach (var thread in waiting)
{
    Console.WriteLine($"  {thread.ThreadId}: {thread.State}, waiting at {string.Join("/", thread.WaitingAt)}");
}

Check(
    waiting.Select(thread => thread.ThreadId).SequenceEqual([AcmeThread, GlobexThread]),
    "expected both transfers to be waiting");

// --- Step 3: a string override that doesn't fit is refused, and the thread keeps waiting. ---

Console.WriteLine();
Console.WriteLine("=== Step 3: a string override for a decimal field is refused before the checkpoint is touched ===");
var stringly = new HumanSignal(true, "jdoe@acme.io", new Dictionary<string, string> { ["Amount"] = "4150.25" });
var refusal = await graph.ResumeAsync(AcmeThread, stringly, restarted);
if (refusal is not GraphExecutionResult<WireTransferState>.Failed refused)
{
    throw new InvalidOperationException($"expected the string override to be refused, got: {refusal}");
}

Console.WriteLine($"  Refused: {refused.Message}");
Check((await restarted.LoadAsync(AcmeThread))!.ResumedAt is null, "a refused resume must not stamp the checkpoint");
Check(bank.Sent.Count == 0, "a refused resume must not run anything");
Console.WriteLine($"  Not stamped, nothing ran: '{AcmeThread}' can still be answered.");

// --- Step 4: approve with a typed override. The transfer node reads the decision. ---

Console.WriteLine();
Console.WriteLine("=== Step 4: jdoe approves wire-acme-corp, correcting the amount to the invoice total ===");
var approval = HumanSignal.Approve("jdoe@acme.io").WithOverride("Amount", 4150.25m);
var approved = Completed(await graph.ResumeAsync(AcmeThread, approval, restarted));
Console.WriteLine($"  completed: {approved}");
var acmeDecision = decisions.Single();
Console.WriteLine($"  ExecuteWireTransfer saw: {Describe(acmeDecision)}");
Check(
    acmeDecision is { IsApproved: true, Boundary: "PrepareWireTransfer", Reviewer: "jdoe@acme.io" }
        && acmeDecision.Overrides["Amount"].GetDecimal() == 4150.25m
        && acmeDecision.DecidedAt.Length > 0,
    "expected the decision to record the approval and its typed override");
Check(approved.Amount == 4150.25m, "expected the typed override to set the amount");
Check(approved.ApprovedBy == "jdoe@acme.io", "expected the reviewer from the recorded decision");
Check(bank.Sent.SequenceEqual(["Acme Corp 4150.25 USD"]), "expected exactly the corrected transfer to be sent");
Console.WriteLine("  ApprovedBy came from ctx.Decision.Reviewer. The signal set only Amount.");

// --- Step 5: reject. The reject route runs; the transfer node never does. ---

Console.WriteLine();
Console.WriteLine("=== Step 5: mlee rejects wire-globex, so the run takes the reject route ===");
var rejection = HumanSignal.Reject("mlee@acme.io").WithOverride("Note", "beneficiary not on the vendor list");
var rejected = Completed(await graph.ResumeAsync(GlobexThread, rejection, restarted));
Console.WriteLine($"  completed: {rejected}");
Console.WriteLine($"  CancelWireTransfer saw: {Describe(decisions[^1])}");
Check(decisions.Count == 2 && decisions[^1] is { IsApproved: false, Reviewer: "mlee@acme.io" }, "expected the rejection to be recorded");
Check(rejected.Status == "cancelled by mlee@acme.io", "expected CancelWireTransfer to run");
Check(rejected.Note == "beneficiary not on the vendor list", "expected the rejection's override to apply too");
Check(bank.Sent.Count == 1, "a rejected transfer must never reach the bank");
Console.WriteLine($"  Transfers sent in total: {bank.Sent.Count}. ExecuteWireTransfer never ran for Globex.");

// --- Step 6: nothing is left waiting. ---

Console.WriteLine();
Console.WriteLine("=== Step 6: the waiting list is empty ===");
var stillWaiting = await ListWaitingAsync(restarted);
Console.WriteLine($"  Threads waiting for a human: {stillWaiting.Count}");
Check(stillWaiting.Count == 0, "expected both answers to have used up their checkpoints");

// DecidedAt (when the resume began) is left out: it changes every run.
static string Describe(HumanDecision decision) =>
    $"{(decision.IsApproved ? "approved" : "rejected")} by {decision.Reviewer} at {decision.Boundary}, overrides "
    + string.Join(", ", decision.Overrides.Select(pair => $"{pair.Key}={pair.Value.GetRawText()}"));

static async Task<IReadOnlyList<ThreadSummary>> ListWaitingAsync(ICheckpointQuery store)
{
    var page = await store.ListThreadsAsync(new ThreadQuery { States = new HashSet<CheckpointThreadState> { CheckpointThreadState.Interrupted } });
    // Newest first is the store's order; both were saved in the same second, so sort for stable output.
    return [.. page.Threads.OrderBy(thread => thread.ThreadId, StringComparer.Ordinal)];
}

static EfCoreCheckpointStore OpenFileBackedStore(string dbPath)
{
    var options = new DbContextOptionsBuilder<CheckpointDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options;
    return new EfCoreCheckpointStore(new CheckpointDbContext(options));
}

static WireTransferState Completed(GraphExecutionResult<WireTransferState> result) =>
    (result as GraphExecutionResult<WireTransferState>.Completed)?.State
    ?? throw new InvalidOperationException($"expected Completed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record WireTransferState(
    string Beneficiary,
    decimal Amount,
    string Currency,
    bool InvoiceGenerated,
    string Status,
    string? ApprovedBy,
    string? Note)
{
    public static WireTransferState New(string beneficiary, decimal amount) =>
        new(beneficiary, amount, "USD", InvoiceGenerated: false, Status: "new", ApprovedBy: null, Note: null);
}

/// <summary>The irreversible side effect, kept in memory so the demo can count what was sent.</summary>
sealed class Bank
{
    public List<string> Sent { get; } = [];

    public string Send(string beneficiary, decimal amount, string currency)
    {
        Sent.Add($"{beneficiary} {amount:F2} {currency}");
        return $"ref {Sent.Count:D4}";
    }
}
