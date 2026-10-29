// Dynamic fan-out: the map (design/27-Map-Fan-Out.md).
//
// An accounts-payable check. An invoice arrives with however many lines it has, and every line runs
// the same small audit graph -- Classify -> CheckPrice -> Score -- before Decide approves the
// invoice or holds it for review:
//
//   AuditLines (map: one LineAudit child per invoice line) -> Decide
//
// Demo 10's parallel group has branches fixed when the graph is built. Demo 13's subgraph runs one
// child graph inside one node. A map runs a compiled child graph once per item of a list it reads
// from the state when the node starts, so how many children run is only known at run time. This
// demo shows:
//
//   1. The fan-out    -- 12 lines, at most 8 at a time by default (MaxConcurrency is never
//                        unbounded), started and reduced in list order, each item's nodes at
//                        AuditLines/<line id>/<node>.
//   2. Bad lists      -- a duplicate key, a key with '/', an empty key, or more lines than MaxItems
//                        fail the map fatally before any line is looked at.
//   3. A failing line -- the child's failure kind becomes the item's and then the map's: a fatal
//                        one is never retried, a transient one carries its retry hint, and a retry
//                        of the map re-runs every line.
//   4. KeepSucceeded  -- an outcomes reducer that sees every line, and flags the one that failed.
//   5. .Compile()     -- refuses a child graph with an interrupt boundary.
//
// The price list is an in-memory dictionary, so the demo runs offline. Where concurrency matters, a
// gate holds price lookups until the expected number are in flight at once, so the peak is exact
// rather than dependent on timing.

using System.Collections.Concurrent;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;

// --- Scenario 1: the fan-out. ---

Console.WriteLine("=== Scenario 1: a 12-line invoice -- one child graph per line, default options ===");
var invoice = Invoice.Of(
    "INV-2207",
    new("L01", "PAP-A4", 40, 4.20m),
    new("L02", "PAP-A3", 10, 8.10m),
    new("L03", "INK-K", 4, 62.00m),
    new("L04", "INK-C", 2, 71.00m),
    new("L05", "PEN-BLU", 200, 0.90m),
    new("L06", "PEN-RED", 50, 0.90m),
    new("L07", "PAD-A5", 60, 2.40m),
    new("L08", "FLD-GRN", 100, 0.35m),
    new("L09", "INK-M", 2, 79.50m),
    new("L10", "SVC-INST", 1, 45.00m),
    new("L11", "PAP-A4", 20, 4.20m),
    new("L12", "SVC-DLV", 1, 12.00m));
var prices = new PriceList { HoldUntilInFlight = 8 };
var recorder = new Recorder();
var result = await InvoiceGraph(prices).Compile().RunAsync("inv-1", invoice, new InMemoryCheckpointStore(), recorder.Options);
await recorder.Finished;
var audited = ExpectCompleted(result);
var expanded = recorder.Events.OfType<MapExpanded>().Single();
var started = recorder.ItemsStarted();
var l07 = recorder.Events.OfType<NodeCompleted>().Where(e => e.NodePath!.StartsWith("AuditLines/L07/", StringComparison.Ordinal)).Select(e => e.NodePath!);
Console.WriteLine($"  MapExpanded: [{string.Join(", ", expanded.Keys)}]");
Console.WriteLine($"  Items started in list order: {string.Join(", ", started)}");
Console.WriteLine(
    $"  Peak price lookups in flight: {prices.Peak} (MapOptions.DefaultMaxConcurrency = {MapOptions<LineAudit>.DefaultMaxConcurrency}); the other {invoice.Lines.Count - prices.Peak} lines waited for a slot.");
Console.WriteLine($"  One item's child nodes: {string.Join(" -> ", l07)}");
Console.WriteLine($"  Child nodes run: {recorder.Events.OfType<NodeCompleted>().Count(e => e.NodePath!.Count(c => c == '/') == 2)} (12 lines x 3 nodes)");
Console.WriteLine("  Reducer got the audits in list order:");
foreach (var audit in audited.Audits)
{
    Console.WriteLine($"    {audit}");
}

Console.WriteLine($"  Decision: {audited.Decision}");
Check(expanded.Keys.SequenceEqual(invoice.Lines.Select(l => l.Id)), "expected MapExpanded to list every line id in list order");
Check(
    recorder.Events.ToList().FindIndex(e => e is MapExpanded) < recorder.Events.ToList().FindIndex(e => e is BranchStarted),
    "expected MapExpanded before the first item starts");
Check(started.SequenceEqual(invoice.Lines.Select(l => l.Id)), "expected items to start in list order");
Check(prices.Peak == 8 && new MapOptions<LineAudit>().MaxConcurrency == 8, "expected the default limit of 8");
Check(audited.Audits.Select(a => a.Line.Id).SequenceEqual(invoice.Lines.Select(l => l.Id)), "expected the reducer to get list order");
Check(l07.SequenceEqual(["AuditLines/L07/Classify", "AuditLines/L07/CheckPrice", "AuditLines/L07/Score"]), "expected <map>/<key>/<node> paths");
Check(audited.Decision == "hold: L09 billed 79.50, list 71.00 (+12%)", "expected L09's overcharge to hold the invoice");

Console.WriteLine();
Console.WriteLine("=== Scenario 1b: the same invoice with MaxConcurrency = 3 (the price API's own limit) ===");
prices = new PriceList { HoldUntilInFlight = 3 };
recorder = new Recorder();
result = await InvoiceGraph(prices, new MapOptions<LineAudit> { MaxConcurrency = 3 }).Compile()
    .RunAsync("inv-1b", invoice, new InMemoryCheckpointStore(), recorder.Options);
await recorder.Finished;
ExpectCompleted(result);
Console.WriteLine($"  Peak price lookups in flight: {prices.Peak}; items still started in list order: {recorder.ItemsStarted().SequenceEqual(invoice.Lines.Select(l => l.Id))}.");
Check(prices.Peak == 3, "expected at most 3 items in flight");

// --- Scenario 2: bad lists fail the map before anything starts. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: bad lists fail before any line is looked at -- even with Retry(3) on the map ===");
var badLists = new (string Problem, Invoice Invoice)[]
{
    ("an exported row repeated", Invoice.Of("INV-2208", new("L01", "PAP-A4", 1, 4.20m), new("L02", "INK-K", 1, 62.00m), new("L02", "INK-K", 1, 62.00m))),
    ("a split line", Invoice.Of("INV-2209", new("L01", "PAP-A4", 1, 4.20m), new("L02/1", "INK-K", 1, 62.00m))),
    ("a line with no id", Invoice.Of("INV-2210", new("L01", "PAP-A4", 1, 4.20m), new("", "INK-K", 1, 62.00m))),
    ("60 lines, limit 50", Invoice.Of("INV-2211", [.. Enumerable.Range(1, 60).Select(i => new InvoiceLine($"L{i:00}", "PEN-BLU", 1, 0.90m))])),
};
foreach (var (problem, bad) in badLists)
{
    prices = new PriceList();
    recorder = new Recorder();
    var store = new InMemoryCheckpointStore();
    var graph = InvoiceGraph(prices, new MapOptions<LineAudit> { MaxItems = 50 })
        .SetRecoveryPolicy("AuditLines", RecoveryPolicy.Retry(3))
        .Compile();
    var failed = ExpectNodeFailed(await graph.RunAsync(bad.Number, bad, store, recorder.Options));
    await recorder.Finished;
    var kind = store.Load(bad.Number)!.Metadata!["error_kind"];
    Console.WriteLine($"  {problem,-26} -> {kind}, attempts {failed.Attempts}, lookups {prices.Lookups.Count}: {failed.Message}");
    Check(failed.Attempts == 1 && kind == "fatal", "expected a fatal failure, not retried");
    Check(prices.Lookups.Count == 0, "expected no line to be looked at");
    Check(!recorder.Events.Any(e => e is MapExpanded or BranchStarted), "expected no MapExpanded and no item started");
}

// --- Scenario 3: a failing line -- the child's kind becomes the map's. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3a: an unknown SKU (fatal) -- Retry(2) on the map, Retry(1) on CheckPrice inside the child ===");
var withUnknownSku = Invoice.Of(
    "INV-2212",
    new("L01", "PAP-A4", 10, 4.20m),
    new("L02", "INK-K", 1, 62.00m),
    new("L03", "INK-X9", 1, 58.00m),
    new("L04", "PEN-BLU", 20, 0.90m));
{
    var store = new InMemoryCheckpointStore();
    var failed = ExpectNodeFailed(await InvoiceGraph(new PriceList())
        .SetRecoveryPolicy("AuditLines", RecoveryPolicy.Retry(2))
        .Compile()
        .RunAsync("inv-3a", withUnknownSku, store));
    var kind = store.Load("inv-3a")!.Metadata!["error_kind"];
    Console.WriteLine($"  Failed at '{failed.Node}' after {failed.Attempts} attempt(s), {kind}:");
    Console.WriteLine($"    {failed.Message}");
    Console.WriteLine("  Fatal in the child, fatal for the item, fatal for the map: neither retry policy ran.");
    Check(failed.Message == "item 'L03': failed at 'CheckPrice' after 1 attempt(s): unknown SKU 'INK-X9'", "expected the item's message");
    Check(failed.Attempts == 1 && kind == "fatal", "expected a fatal map failure with no retry");
}

Console.WriteLine();
Console.WriteLine("=== Scenario 3b: the price API is busy (transient, retry in 2 s) -- one request at a time ===");
var small = Invoice.Of("INV-2213", new("L01", "PAP-A4", 10, 4.20m), new("L02", "INK-K", 1, 62.00m), new("L03", "PEN-BLU", 20, 0.90m));
var oneAtATime = new MapOptions<LineAudit> { MaxConcurrency = 1 };
{
    var store = new InMemoryCheckpointStore();
    prices = new PriceList { BusyFor = { ["INK-K"] = 2 } };
    var failed = ExpectNodeFailed(await InvoiceGraph(prices, oneAtATime).Compile().RunAsync("inv-3b", small, store));
    var metadata = store.Load("inv-3b")!.Metadata!;
    Console.WriteLine($"  No retry on the map: failed, {metadata["error_kind"]}, strata.retry_after_ms = {metadata["strata.retry_after_ms"]}:");
    Console.WriteLine($"    {failed.Message}");
    Console.WriteLine($"  Lookups: {string.Join(", ", prices.Lookups)} -- L03 never started.");
    Check(failed.Message == "item 'L02': failed at 'CheckPrice' after 2 attempt(s): price service busy (429)", "expected the child's retries to have run out");
    Check(metadata["error_kind"] == "transient" && metadata["strata.retry_after_ms"] == "2000", "expected the item's kind and hint on the map");
    Check(prices.Lookups.SequenceEqual(["L01", "L02", "L02"]), "expected L03 never to start");
}

{
    prices = new PriceList { BusyFor = { ["INK-K"] = 2 } };
    recorder = new Recorder();
    result = await InvoiceGraph(prices, oneAtATime)
        .SetRecoveryPolicy("AuditLines", RecoveryPolicy.Retry(1))
        .Compile()
        .RunAsync("inv-3c", small, new InMemoryCheckpointStore(), recorder.Options);
    await recorder.Finished;
    var done = ExpectCompleted(result);
    var expansions = recorder.Events.OfType<MapExpanded>().Select(e => e.Attempt).ToList();
    Console.WriteLine($"  With Retry(1) on the map: {done.Decision}. MapExpanded on attempts {string.Join(" and ", expansions)}.");
    Console.WriteLine($"  Lookups: {string.Join(", ", prices.Lookups)} -- the retry re-ran L01, which had succeeded.");
    Check(expansions.SequenceEqual([1, 2]), "expected one MapExpanded per attempt");
    Check(prices.Lookups.SequenceEqual(["L01", "L02", "L02", "L01", "L02", "L03"]), "expected the retry to re-run every line");
}

// --- Scenario 4: KeepSucceeded with an outcomes reducer. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: Partial = KeepSucceeded -- audit what can be audited, flag the rest ===");
var mixed = Invoice.Of(
    "INV-2214",
    new("L01", "PAP-A4", 10, 4.20m),
    new("L02", "INK-K", 1, 62.00m),
    new("L03", "INK-X9", 1, 58.00m),
    new("L04", "PEN-BLU", 20, 0.90m),
    new("L05", "SVC-INST", 1, 52.00m));
recorder = new Recorder();
BranchOutcomes<LineAudit>? reduced = null;
var keepGraph = new StateGraph<Invoice>()
    .AddMapWithOutcomes(
        "AuditLines",
        inv => inv.Lines,
        line => line.Id,
        LineAuditGraph(new PriceList()),
        (_, line) => LineAudit.For(line),
        (inv, results) =>
        {
            reduced = results;
            return inv with
            {
                Audits = [.. results.Succeeded],
                Review = [.. results.Where(r => r.Status != BranchStatus.Succeeded).Select(r => $"{r.Name} not audited: {r.Error?.Message}")],
            };
        },
        new MapOptions<LineAudit> { Partial = PartialResults.KeepSucceeded })
    .AddNode("Decide", Decide)
    .AddEdge("AuditLines", "Decide")
    .Compile();
var kept = ExpectCompleted(await keepGraph.RunAsync("inv-4", mixed, new InMemoryCheckpointStore(), recorder.Options));
await recorder.Finished;
var seen = reduced ?? throw new InvalidOperationException("expected the reducer to run");
Console.WriteLine($"  Reducer saw (list order): {string.Join(", ", seen.Select(o => $"{o.Name} {o.Status}"))}; JoinMet: {seen.JoinMet}");
Console.WriteLine($"  L03's error: {seen["L03"]!.Error!.Kind}, \"{seen["L03"]!.Error!.Message}\"");
Console.WriteLine($"  Decision: {kept.Decision}");
foreach (var line in kept.Review)
{
    Console.WriteLine($"    - {line}");
}

Check(
    seen.Select(o => o.Status).SequenceEqual(
        [BranchStatus.Succeeded, BranchStatus.Succeeded, BranchStatus.Failed, BranchStatus.Succeeded, BranchStatus.Succeeded]),
    "expected every line's outcome, in list order");
Check(seen["L03"]!.Error is { Kind: NodeFailureKind.Fatal, Message: "failed at 'CheckPrice' after 1 attempt(s): unknown SKU 'INK-X9'" }, "expected L03's own error");
Check(seen is { JoinMet: false, Deciding: [] }, "expected an unmet All join");
Check(!recorder.Events.OfType<BranchCompleted>().Any(e => e.Result == BranchResult.Cancelled), "expected no line to be cancelled for L03's failure");
Check(kept.Review.Count == 2 && kept.Decision == "hold: 2 line(s) need review", "expected L03 and L05 to need review");

// --- Step 5: what .Compile() and the options refuse. ---

Console.WriteLine();
Console.WriteLine("=== Step 5: a child graph that could pause for a human is refused at .Compile() ===");
var signOff = new StateGraph<LineAudit>()
    .AddNode("Classify", a => a)
    .AddNode("Score", a => a)
    .AddEdge("Classify", "Score")
    .AddInterruptBoundary("Score")
    .Compile();

// The same boundary one level down, inside a subgraph of the child, is found too.
var withReview = new StateGraph<LineAudit>()
    .AddNode("CheckPrice", a => a)
    .AddSubgraph("Review", signOff, a => a, (_, reviewed) => reviewed)
    .AddEdge("CheckPrice", "Review")
    .Compile();
foreach (var (child, at) in new[] { (signOff, "Score"), (withReview, "Review/Score") })
{
    try
    {
        new StateGraph<Invoice>()
            .AddMap("AuditLines", inv => inv.Lines, line => line.Id, child, (_, line) => LineAudit.For(line), (inv, _) => inv)
            .Compile();
        throw new InvalidOperationException("expected a child with an interrupt boundary to be refused");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("can't freeze", StringComparison.Ordinal))
    {
        Console.WriteLine($"  Refused: {ex.Message}");
        Check(
            ex.Message == $"map 'AuditLines' runs a graph with an interrupt boundary at '{at}'; a map item can't freeze",
            "expected the boundary to be named by its path");
    }
}

try
{
    _ = new MapOptions<LineAudit> { MaxConcurrency = 0 };
    throw new InvalidOperationException("expected MaxConcurrency = 0 to be refused");
}
catch (ArgumentOutOfRangeException)
{
    Console.WriteLine("  MaxConcurrency = 0 is refused too (ArgumentOutOfRangeException): a map is never unbounded.");
}

Console.WriteLine();
Console.WriteLine("=== Done: one child graph per line, bounded, keyed, and failing the way its children failed. ===");

// The parent: AuditLines (a map over the invoice's lines, keyed by line id) -> Decide. Returned
// uncompiled so a scenario can add a recovery policy first.
static StateGraph<Invoice> InvoiceGraph(PriceList prices, MapOptions<LineAudit>? options = null) =>
    new StateGraph<Invoice>()
        .AddMap(
            "AuditLines",
            inv => inv.Lines,
            line => line.Id,
            LineAuditGraph(prices),
            (_, line) => LineAudit.For(line),
            (inv, audits) => inv with { Audits = [.. audits] },
            options)
        .AddNode("Decide", Decide)
        .AddEdge("AuditLines", "Decide");

// The child, run once per line: Classify -> CheckPrice -> Score. CheckPrice retries a transient
// failure once on its own, inside the item, before the item fails.
static CompiledGraph<LineAudit> LineAuditGraph(PriceList prices) =>
    new StateGraph<LineAudit>()
        .AddNode("Classify", audit => audit with { Category = Category(audit.Line.Sku) })
        .AddFallibleNode("CheckPrice", async (audit, _, ct) => audit with { ListPrice = await prices.LookUpAsync(audit.Line, ct) })
        .AddNode("Score", Score)
        .AddEdge("Classify", "CheckPrice")
        .AddEdge("CheckPrice", "Score")
        .SetRecoveryPolicy("CheckPrice", RecoveryPolicy.Retry(1))
        .Compile();

static string Category(string sku) => sku.Split('-')[0] switch
{
    "PAP" or "PAD" => "paper",
    "INK" => "toner",
    "PEN" or "FLD" => "stationery",
    "SVC" => "service",
    _ => "other",
};

// 100 at or below list price, 4 points off per percent over it.
static LineAudit Score(LineAudit audit)
{
    var list = audit.ListPrice!.Value;
    var over = (int)Math.Round((audit.Line.UnitPrice - list) / list * 100);
    return over <= 0
        ? audit with { Score = 100 }
        : audit with
        {
            Score = Math.Max(0, 100 - (4 * over)),
            Note = $"billed {audit.Line.UnitPrice:0.00}, list {list:0.00} (+{over}%)",
        };
}

// Any line scoring under 80 (or not audited at all) holds the invoice.
static Invoice Decide(Invoice invoice)
{
    List<string> review = [.. invoice.Review, .. invoice.Audits.Where(a => a.Score < 80).Select(a => $"{a.Line.Id} {a.Note}")];
    var decision = review.Count switch
    {
        0 => "approve",
        1 when invoice.Review.Count == 0 => $"hold: {review[0]}",
        _ => $"hold: {review.Count} line(s) need review",
    };
    return invoice with { Review = review, Decision = decision };
}

static T ExpectCompleted<T>(GraphExecutionResult<T> result) =>
    result is GraphExecutionResult<T>.Completed completed
        ? completed.State
        : throw new InvalidOperationException($"expected Completed, got: {result}");

static GraphExecutionResult<T>.NodeFailed ExpectNodeFailed<T>(GraphExecutionResult<T> result) =>
    result as GraphExecutionResult<T>.NodeFailed ?? throw new InvalidOperationException($"expected NodeFailed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record InvoiceLine(string Id, string Sku, int Quantity, decimal UnitPrice);

/// <summary>The parent's state: the lines to audit, the audits that came back, and the decision.</summary>
sealed record Invoice(
    string Number,
    IReadOnlyList<InvoiceLine> Lines,
    IReadOnlyList<LineAudit> Audits,
    IReadOnlyList<string> Review,
    string? Decision)
{
    public static Invoice Of(string number, params InvoiceLine[] lines) => new(number, lines, [], [], null);
}

/// <summary>One item's child state: the line, and what Classify, CheckPrice and Score found.</summary>
sealed record LineAudit(InvoiceLine Line, string? Category, decimal? ListPrice, int? Score, string? Note)
{
    public static LineAudit For(InvoiceLine line) => new(line, null, null, null, null);

    public override string ToString() =>
        $"{Line.Id} {Line.Sku,-8} {Category,-10} score {Score,3}{(Note is null ? string.Empty : $"  {Note}")}";
}

/// <summary>
/// The supplier's price list. It records every lookup and the peak number in flight; it can hold
/// lookups until a number are in flight at once (so a peak is exact, not a matter of timing), and
/// answer "busy" for a SKU's first few lookups.
/// </summary>
sealed class PriceList
{
    private static readonly Dictionary<string, decimal> Prices = new()
    {
        ["PAP-A4"] = 4.20m,
        ["PAP-A3"] = 8.10m,
        ["PAD-A5"] = 2.40m,
        ["INK-K"] = 62.00m,
        ["INK-C"] = 71.00m,
        ["INK-M"] = 71.00m,
        ["PEN-BLU"] = 0.90m,
        ["PEN-RED"] = 0.90m,
        ["FLD-GRN"] = 0.35m,
        ["SVC-INST"] = 45.00m,
        ["SVC-DLV"] = 12.00m,
    };

    private readonly ConcurrentQueue<string> _lookups = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlight;
    private int _peak;

    /// <summary>Hold each lookup until this many are in flight at once (0: don't hold).</summary>
    public int HoldUntilInFlight { get; init; }

    /// <summary>SKU -> how many of its first lookups answer "busy".</summary>
    public Dictionary<string, int> BusyFor { get; } = [];

    /// <summary>The line id of every lookup, in the order they arrived.</summary>
    public IReadOnlyList<string> Lookups => [.. _lookups];

    public int Peak => Volatile.Read(ref _peak);

    public async Task<decimal> LookUpAsync(InvoiceLine line, CancellationToken ct)
    {
        _lookups.Enqueue(line.Id);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
        {
        }

        try
        {
            // Once the first wave is in flight together, the gate stays open for every later lookup.
            if (now >= HoldUntilInFlight)
            {
                _gate.TrySetResult();
            }

            await _gate.Task.WaitAsync(ct);

            var call = _calls.AddOrUpdate(line.Sku, 1, (_, n) => n + 1);
            if (call <= BusyFor.GetValueOrDefault(line.Sku))
            {
                throw NodeException.Transient("price service busy (429)", TimeSpan.FromSeconds(2));
            }

            return Prices.TryGetValue(line.Sku, out var price)
                ? price
                : throw NodeException.Fatal($"unknown SKU '{line.Sku}'");
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>Keeps a run's events, and says when the run's last one has arrived.</summary>
sealed class Recorder : IRunObserver
{
    private readonly ConcurrentQueue<RunEvent> _events = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunOptions Options => new() { Observer = this };

    public IReadOnlyList<RunEvent> Events => [.. _events];

    /// <summary>
    /// Completes with the run's last event. Events are delivered off the executor, so the last ones can
    /// arrive after RunAsync returns.
    /// </summary>
    public Task Finished => _finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>The keys of the items the map started, in the order it started them.</summary>
    public IReadOnlyList<string> ItemsStarted() =>
        [.. Events.OfType<BranchStarted>().Select(e => e.NodePath!["AuditLines/".Length..])];

    public void OnEvent(RunEvent runEvent)
    {
        _events.Enqueue(runEvent);
        if (runEvent is RunCompleted)
        {
            _finished.TrySetResult();
        }
    }
}
