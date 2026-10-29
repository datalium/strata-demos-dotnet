// Multi-node graphs: a "diamond" shaped graph combining a linear chain
// (`.AddEdge(...)`) with a conditional branch (`.AddConditionalEdge(...)`)
// that converges back to a shared node -- the first graph in this suite with
// more than one node.
//
// CalculateSubtotal -> ApplyDiscount -> (branch on order size) ->
// TagBulkShipping or TagStandardShipping -> FormatReceipt.
//
// Also demonstrates that execution order comes from edges and the entry
// point, not from the order nodes are passed to `.AddNode(...)`: nodes are
// deliberately added out of run order below, and `.SetEntryPoint(...)`
// overrides the "first node added" default that 01-hello-agent relies on.

using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;

// "FormatReceipt" is added first -- without `.SetEntryPoint(...)` below it
// would silently become the entry point (per AddNode's "first node added"
// rule) and the graph would run receipt-formatting on half-populated state
// before anything computed a subtotal.
var graph = new StateGraph<OrderState>()
    .AddNode("FormatReceipt", state =>
    {
        var receipt = $"{state.Quantity}x {state.Item} @ ${state.UnitPrice:F2} = " +
            $"${state.Subtotal!.Value:F2} (- ${state.Discount!.Value:F2} discount) => ${state.Total!.Value:F2} " +
            $"[{state.ShippingNote}]";
        return state with { Receipt = receipt };
    })
    .AddNode("CalculateSubtotal", state => state with { Subtotal = state.Quantity * state.UnitPrice })
    .AddNode("ApplyDiscount", state =>
    {
        var subtotal = state.Subtotal!.Value;
        // Bulk orders of $100+ get a flat 10% off.
        var discount = subtotal >= 100.0 ? subtotal * 0.10 : 0.0;
        return state with { Discount = discount, Total = subtotal - discount };
    })
    .AddNode("TagBulkShipping", state => state with { ShippingNote = "bulk order, ships via freight" })
    .AddNode("TagStandardShipping", state => state with { ShippingNote = "standard shipping" })
    .SetEntryPoint("CalculateSubtotal")
    .AddEdge("CalculateSubtotal", "ApplyDiscount")
    // The conditional edge is the "if" (10+ units ships freight); the plain
    // edge added right after it is the "else" -- RunAsync tries a node's
    // outgoing edges in the order they were added and takes the first whose
    // condition matches, so an unconditional edge added after one or more
    // conditional edges is the default branch. This condition (quantity) is
    // deliberately independent of ApplyDiscount's dollar-amount threshold
    // above, so the two sample orders below each exercise a different
    // combination.
    .AddConditionalEdge("ApplyDiscount", "TagBulkShipping", state => state.Quantity >= 10)
    .AddEdge("ApplyDiscount", "TagStandardShipping")
    .AddEdge("TagBulkShipping", "FormatReceipt")
    .AddEdge("TagStandardShipping", "FormatReceipt")
    .Compile();

ICheckpointStore store = new InMemoryCheckpointStore();
// Each order names the branch it must take, so the run proves both fire.
var orders = new (string ThreadId, OrderState Initial, string ExpectedShipping)[]
{
    ("bulk-order-thread", new OrderState { Item = "widget", Quantity = 12, UnitPrice = 9.50 }, "bulk order, ships via freight"),
    ("standard-order-thread", new OrderState { Item = "widget", Quantity = 3, UnitPrice = 9.50 }, "standard shipping"),
};

foreach (var (threadId, initial, expectedShipping) in orders)
{
    var result = await graph.RunAsync(threadId, initial, store);

    switch (result)
    {
        case GraphExecutionResult<OrderState>.Completed completed:
            Console.WriteLine(completed.State.Receipt);
            if (completed.State.ShippingNote != expectedShipping)
            {
                Console.Error.WriteLine($"CHECK FAILED: {threadId} took the wrong branch ({completed.State.ShippingNote})");
                Environment.Exit(1);
            }

            break;
        case GraphExecutionResult<OrderState>.Failed failed:
            throw new InvalidOperationException($"graph execution failed: {failed.Message}");
        default:
            throw new InvalidOperationException($"unexpected result: {result}");
    }
}

sealed record OrderState
{
    public required string Item { get; init; }
    public required int Quantity { get; init; }
    public required double UnitPrice { get; init; }
    public double? Subtotal { get; init; }
    public double? Discount { get; init; }
    public double? Total { get; init; }
    public string? ShippingNote { get; init; }
    public string? Receipt { get; init; }
}
