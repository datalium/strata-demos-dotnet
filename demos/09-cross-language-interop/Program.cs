// Cross-language interop: freeze a graph in one runtime, resume it in the other.
//
// Strata's "one wire format" rule (CLAUDE.md in datalium/strata) is that the Rust `strata` CLI and
// runtime can read checkpoints the C# runtime produced, and vice versa. Until this demo that was only proven at fixture level
// (both runtimes' tests agree on a JSON shape); no checkpoint had ever crossed a real file between
// the two. This demo is the same wire-transfer graph as 05-hitl-interruption, split across two
// *processes* and two *runtimes* through one SQLite file:
//
//   dotnet run --project demos/09-cross-language-interop/09-cross-language-interop.csproj -- freeze   # .NET freezes ...
//   cargo run -p cross-language-interop -- resume   # (in ../strata-demos-rust)                # ... Rust resumes
//
// and the other way around (Rust freeze, then `dotnet run ... -- resume`). The Rust twin of this
// file is demos/09-cross-language-interop/src/main.rs in the sibling repo; the two define the same
// graph, thread id, and state shape independently, and the only thing they share is the database
// file.
//
// Each node stamps the state with the runtime that ran it (frozen_by, executed_by), so the
// completed state shows the hand-off: frozen_by: dotnet, executed_by: rust, or the reverse.
//
// Modes: `freeze [path]` and `resume [path]`. `path` defaults to
// strata-demo-09-interop/checkpoints.sqlite under the system temp dir -- the Rust twin computes the
// same default, so no argument is needed for the two to find each other.

using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage.Memory;
using Microsoft.EntityFrameworkCore;

const string runtime = "dotnet";
const string threadId = "wire-transfer-xlang";

var mode = args.Length > 0 ? args[0] : null;
var dbPath = args.Length > 1
    ? args[1]
    : Path.Combine(Path.GetTempPath(), "strata-demo-09-interop", "checkpoints.sqlite");

var graph = new StateGraph<WireTransferState>()
    .AddNode("PrepareWireTransfer", state => state with { InvoiceGenerated = true, FrozenBy = runtime })
    .AddInterruptBoundary("PrepareWireTransfer")
    .AddNode("ExecuteWireTransfer", state => state with { IsCompleted = true, ExecutedBy = runtime })
    .AddEdge("PrepareWireTransfer", "ExecuteWireTransfer")
    .Compile();

switch (mode)
{
    case "freeze":
        return await FreezeAsync();
    case "resume":
        return await ResumeAsync();
    default:
        Console.Error.WriteLine("usage: 09-cross-language-interop <freeze|resume> [checkpoint-log-path]");
        return 2;
}

async Task<int> FreezeAsync()
{
    // A fresh log each time: `freeze` always starts a new thread history.
    if (File.Exists(dbPath))
    {
        File.Delete(dbPath);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

    Console.WriteLine($"=== [{runtime}] freeze: run to the interrupt boundary ===");
    Console.WriteLine($"  Checkpoint log: {dbPath}");

    var initial = new WireTransferState(
        InvoiceGenerated: false,
        Amount: 4200.50,
        Currency: "USD",
        ApprovedBy: null,
        IsCompleted: false,
        FrozenBy: null,
        ExecutedBy: null);

    using var store = OpenFileBackedStore(dbPath);
    var result = await graph.RunAsync(threadId, initial, store);
    if (result is not GraphExecutionResult<WireTransferState>.Interrupted interrupted)
    {
        Console.Error.WriteLine($"error: expected Interrupted, got: {result}");
        return 1;
    }

    Console.WriteLine(
        $"  Interrupted at '{interrupted.SuspendedNode}' (thread '{interrupted.ThreadId}'); next node on resume: '{interrupted.NextNode}'");
    Console.WriteLine($"  Frozen state: {interrupted.FrozenState}");
    Console.WriteLine();
    Console.WriteLine("  This process is about to exit; the checkpoint now exists only in the file.");
    Console.WriteLine("  Next: run the `resume` step -- from the other runtime to see the hand-off.");
    return 0;
}

async Task<int> ResumeAsync()
{
    Console.WriteLine($"=== [{runtime}] resume: pick up a checkpoint another process froze ===");
    Console.WriteLine($"  Checkpoint log: {dbPath}");
    if (!File.Exists(dbPath))
    {
        Console.Error.WriteLine("error: no checkpoint log there -- run the `freeze` step first");
        return 1;
    }

    // The override is keyed by the *JSON* name (approved_by), not the C# property name: it is merged
    // into the frozen state's JSON before that is rehydrated into WireTransferState.
    var signal = new HumanSignal(
        IsApproved: true,
        ReviewerToken: "token-xyz",
        FieldOverrides: new Dictionary<string, string> { ["approved_by"] = "compliance_officer_07" });

    using var store = OpenFileBackedStore(dbPath);
    var result = await graph.ResumeAsync(threadId, signal, store);
    switch (result)
    {
        case GraphExecutionResult<WireTransferState>.Completed completed:
            Console.WriteLine("  Resumed and ran to completion.");
            Console.WriteLine($"  Completed state: {completed.State}");
            Console.WriteLine();
            Console.WriteLine(
                $"  frozen_by = \"{completed.State.FrozenBy ?? "?"}\", executed_by = \"{completed.State.ExecutedBy ?? "?"}\"");
            return 0;
        case GraphExecutionResult<WireTransferState>.Failed failed:
            Console.Error.WriteLine(
                $"error: resume failed: {failed.Message} (was this thread already resumed? run `freeze` again)");
            return 1;
        default:
            Console.Error.WriteLine($"error: expected Completed, got: {result}");
            return 1;
    }
}

static EfCoreCheckpointStore OpenFileBackedStore(string dbPath)
{
    var options = new DbContextOptionsBuilder<CheckpointDbContext>()
        .UseSqlite($"Data Source={dbPath}")
        .Options;
    return new EfCoreCheckpointStore(new CheckpointDbContext(options));
}

// Field names are snake_case on the wire, and the Rust twin's struct uses the same names. That
// matters beyond style: the state is user-defined JSON inside the checkpoint, so nothing but this
// agreement keeps the two runtimes' WireTransferStates compatible. Without [JsonPropertyName],
// System.Text.Json would write PascalCase (InvoiceGenerated) and serde would not find its fields.
sealed record WireTransferState(
    [property: JsonPropertyName("invoice_generated")] bool InvoiceGenerated,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("approved_by")] string? ApprovedBy,
    [property: JsonPropertyName("is_completed")] bool IsCompleted,
    [property: JsonPropertyName("frozen_by")] string? FrozenBy,
    [property: JsonPropertyName("executed_by")] string? ExecutedBy);
