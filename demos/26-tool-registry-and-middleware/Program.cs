// Tool registry, tool context and tool middleware (design/20-Tool-Dispatch-And-Registry.md,
// design/23-Tool-Middleware.md).
//
// Demo 02 dispatches one generated provider directly. Real agents have several providers, and each
// agent should see only some of their tools. This demo puts three [StrataTool] providers into one
// ToolRegistry and walks through what the registry adds:
//
//   1. One registry, many providers -- merged manifest in registration order; a duplicate tool name
//                                       from another provider is refused at registration.
//   2. Per-agent subsets             -- Subset(...) refuses a left-out tool as *not permitted*, which
//                                       is distinct from *no such tool*.
//   3. The tool sees its caller      -- ToolCallContext.From(nodeContext) carries thread, run, node
//                                       path, attempt and run metadata; a tool takes the context and a
//                                       CancellationToken as ordinary parameters. ToolCallStarted /
//                                       ToolCallCompleted run events report each call.
//   4. Middleware                    -- Use(...) adds an audit log (outermost), a refund policy that
//                                       denies, an argument rewrite and a cache that short-circuits.
//                                       Refused calls skip the chain and reach OnRefusedAsync.
//
// No LLM: the "agent" is a node that makes a fixed list of calls, as a model's tool-call loop would.

using System.Text.Json;
using Datalium.Strata.Demos.ToolRegistryAndMiddleware;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Datalium.Strata.Tools;

// --- Step 1: one registry, three providers. ---

Console.WriteLine("=== Step 1: one registry, three [StrataTool] providers ===");
var backend = new Backend();
var registry = NewRegistry(backend);
Console.WriteLine("  Registered, in registration order (parameters from the merged manifest):");
foreach (var tool in registry.Tools)
{
    Console.WriteLine($"    {tool.Name}({string.Join(", ", ParameterNames(tool))})");
}

var reserveParameters = ParameterNames(registry.Tools.Single(t => t.Name == "inventory.reserve"));
Console.WriteLine(
    $"  inventory.reserve takes (sku, ToolCallContext, count, CancellationToken); its manifest lists only ({string.Join(", ", reserveParameters)}).");
Check(
    registry.Tools.Select(t => t.Name).SequenceEqual(
        ["inventory.lookup", "inventory.reserve", "billing.refund", "diag.whoami"]),
    "expected the tools in registration order");
Check(reserveParameters.SequenceEqual(["sku", "count"]), "expected the context and token left out of the manifest");

try
{
    registry.Register(new LegacyInventoryTools());
    Check(false, "expected a duplicate tool name to be refused");
}
catch (DuplicateToolException duplicate)
{
    Console.WriteLine($"  Registering LegacyInventoryTools: refused -- {duplicate.Message}");
}

Console.WriteLine(
    $"  Its other tool, inventory.audit, was not registered either: {registry.Tools.Count} tools, none of them inventory.audit.");
Check(
    registry.Tools.Count == 4 && registry.Tools.All(t => t.Name != "inventory.audit"),
    "expected nothing from a refused provider to be registered");

// --- Step 2: per-agent subsets. ---

Console.WriteLine();
Console.WriteLine("=== Step 2: a per-agent Subset(...) ===");
var support = registry.Subset("diag.whoami", "inventory.reserve", "inventory.lookup");
Console.WriteLine($"  support-agent's subset, asked for in another order: {string.Join(", ", support.Tools.Select(t => t.Name))}");
Check(
    support.Tools.Select(t => t.Name).SequenceEqual(["inventory.lookup", "inventory.reserve", "diag.whoami"]),
    "expected a subset to keep the registry's order");

var stock = Result(await support.DispatchToolAsync("inventory.lookup", """{"sku":"AB-12"}"""));
Console.WriteLine($"  inventory.lookup AB-12 -> {stock}");

var refused = await Refusal(() => support.DispatchToolAsync("billing.refund", """{"orderId":"A-1","amount":5}"""));
Console.WriteLine($"  billing.refund   -> {refused.GetType().Name}: {refused.Message}");
Check(refused is ToolNotPermittedException, "expected a left-out tool to be not permitted");

var missing = await Refusal(() => support.DispatchToolAsync("inventory.restock", "{}"));
Console.WriteLine($"  inventory.restock -> {missing.GetType().Name}: {missing.Message}");
Check(missing is not ToolNotPermittedException, "expected an unknown tool to be not found, not 'not permitted'");

try
{
    support.Subset("billing.refund");
    Check(false, "expected a subset to be unable to widen");
}
catch (ToolNotPermittedException widen)
{
    Console.WriteLine($"  support.Subset(\"billing.refund\") -> {widen.GetType().Name}: a subset can only narrow.");
}

Console.WriteLine(
    "  ToolNotPermittedException derives from ToolNotFoundException, so code that tells a model \"no such tool\" covers both.");
Check(backend.CallsTo("billing.refund") == 0, "expected billing.refund never to run");

// --- Step 3: the tool sees its caller. ---

Console.WriteLine();
Console.WriteLine("=== Step 3: ToolCallContext.From(nodeContext) -- the tool sees its caller ===");
var outcomes = new List<string>();
var investigate = new StateGraph<Ticket>()
    .AddNode("Investigate", async (Ticket ticket, NodeContext ctx, CancellationToken ct) =>
    {
        // Copy what the node knows, then add who is calling. CallId is left unset: the registry
        // generates one, as it would when a model's tool call carries no id.
        var call = ToolCallContext.From(ctx) with { CallerId = "support-agent" };
        outcomes.Add(Result(await support.DispatchToolAsync("diag.whoami", "{}", call, ct)));
        outcomes.Add(Result(await support.DispatchToolAsync("inventory.reserve", """{"sku":"AB-12","count":2}""", call, ct)));

        // A token that has already fired stops the call before the tool (or any middleware) runs.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        try
        {
            await support.DispatchToolAsync("inventory.reserve", """{"sku":"AB-12","count":1}""", call, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            outcomes.Add("cancelled before the tool ran");
        }

        return ticket;
    })
    .Compile();
var graph = new StateGraph<Ticket>()
    .AddSubgraph("Triage", investigate, ticket => ticket, (_, ticket) => ticket)
    .Compile();
var channel = new RunEventChannel();
var run = await graph.RunAsync(
    "ticket-T-1001",
    new Ticket("T-1001"),
    new InMemoryCheckpointStore(),
    new RunOptions { Metadata = { ["tenant"] = "acme", ["ticket"] = "T-1001" }, Observer = channel });
var events = await Drain(channel);
Check(run is GraphExecutionResult<Ticket>.Completed, $"expected the run to complete, got {run}");

var seen = backend.LastContext!;
Console.WriteLine("  What inventory.reserve was handed:");
Console.WriteLine($"    ThreadId:  {seen.ThreadId}");
Console.WriteLine($"    RunId:     {(IsUuidV7(seen.RunId) ? "(a UUID v7, generated per run)" : seen.RunId)}");
Console.WriteLine($"    NodePath:  {seen.NodePath}");
Console.WriteLine($"    Attempt:   {seen.Attempt}");
Console.WriteLine($"    Metadata:  {string.Join(", ", seen.Metadata.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}"))}");
Console.WriteLine($"    CallerId:  {seen.CallerId}");
Console.WriteLine($"    CallId:    {(IsUuidV7(seen.CallId) ? "(a UUID v7, generated by the registry)" : seen.CallId)}");
Console.WriteLine("  Results:");
foreach (var outcome in outcomes)
{
    Console.WriteLine($"    {outcome}");
}

Console.WriteLine("  Tool-call run events (the arguments and results are not in them -- they can hold secrets):");
foreach (var e in events.Where(e => e is ToolCallStarted or ToolCallCompleted))
{
    Console.WriteLine($"    #{e.Sequence,-2} {Describe(e)}");
}

Check(
    seen is { ThreadId: "ticket-T-1001", NodePath: "Triage/Investigate", Attempt: 1, CallerId: "support-agent" },
    "expected the tool to see the thread, the qualified node path, the attempt and the caller");
Check(seen.Metadata.GetValueOrDefault("tenant") == "acme", "expected the run metadata on the tool's context");
Check(IsUuidV7(seen.CallId), "expected the registry to generate a UUID v7 call id");
Check(seen.RunId == events[0].RunId, "expected the tool's run id to be the run's");
Check(backend.CallsTo("inventory.reserve") == 1, "expected the already-cancelled call never to reach the tool");
Check(
    events.OfType<ToolCallCompleted>().Select(c => $"{c.Tool} {c.Result}").SequenceEqual(
        ["diag.whoami Succeeded", "inventory.reserve Succeeded", "inventory.reserve Cancelled"]),
    "expected one ToolCallCompleted per call, the last one cancelled");
Check(
    events.Where(e => e is ToolCallStarted or ToolCallCompleted).All(e => e.NodePath == "Triage/Investigate" && e.Attempt == 1),
    "expected the tool-call events at the calling node's path and attempt");

// --- Step 4: middleware. ---

Console.WriteLine();
Console.WriteLine("=== Step 4: middleware -- audit, policy, rewrite, cache ===");
backend = new Backend();
var trace = new ChainTrace();
backend.Trace = trace;
var audit = new AuditLog(trace);
registry = NewRegistry(backend)
    .Use(audit)
    .Use(new RefundPolicy(trace, new Dictionary<string, decimal> { ["acme"] = 100m }))
    .Use(new NormalizeSku(trace))
    .Use(new ReadCache(trace, "inventory.lookup"));
// Made after Use(...), so the subset shares the chain.
var billingAgent = registry.Subset("inventory.lookup", "billing.refund");

(string Tool, string Arguments)[] script =
[
    ("inventory.lookup", """{"sku":" ab-12 "}"""),
    ("inventory.lookup", """{"sku":"AB-12"}"""),
    ("billing.refund", """{"orderId":"A-1001","amount":40}"""),
    ("billing.refund", """{"orderId":"A-1002","amount":250}"""),
    ("inventory.reserve", """{"sku":"AB-12","count":1}"""),
    ("inventory.restock", """{"sku":"AB-12"}"""),
];
var told = new List<string>();
var chains = new List<string>();
var agent = new StateGraph<Ticket>()
    .AddNode("BillingAgent", async (Ticket ticket, NodeContext ctx, CancellationToken ct) =>
    {
        for (var i = 0; i < script.Length; i++)
        {
            var (tool, arguments) = script[i];
            var call = ToolCallContext.From(ctx) with { CallerId = "billing-agent", CallId = $"call_{i + 1}" };
            told.Add($"{call.CallId} {tool} {arguments} -> {await Tell(billingAgent, tool, arguments, call, ct)}");
            chains.Add(trace.Drain());
        }

        return ticket;
    })
    .Compile();
channel = new RunEventChannel();
run = await agent.RunAsync(
    "ticket-T-1002",
    new Ticket("T-1002"),
    new InMemoryCheckpointStore(),
    new RunOptions { Metadata = { ["tenant"] = "acme" }, Observer = channel });
events = await Drain(channel);
Check(run is GraphExecutionResult<Ticket>.Completed, $"expected the run to complete, got {run}");

Console.WriteLine("  What the agent was told for each call (what a model would see):");
foreach (var line in told)
{
    Console.WriteLine($"    {line}");
}

Console.WriteLine("  The chain each call took (registration order, outermost first; refused calls skip it):");
for (var i = 0; i < chains.Count; i++)
{
    Console.WriteLine($"    call_{i + 1}: {(chains[i].Length == 0 ? "(none)" : chains[i])}");
}

Console.WriteLine("  Audit log (outermost, so it sees denials, cache hits and refusals too):");
foreach (var line in audit.Lines)
{
    Console.WriteLine($"    {line}");
}

Console.WriteLine($"  Calls that reached a tool: {string.Join("; ", backend.Calls)}");
Console.WriteLine("  ToolCallCompleted events:");
foreach (var completed in events.OfType<ToolCallCompleted>())
{
    Console.WriteLine(
        $"    {completed.CallId} {completed.Tool}: {completed.Result}{(completed.Error is { } error ? $" -- {error}" : "")}");
}

Check(
    backend.Calls.SequenceEqual(["inventory.lookup AB-12", "billing.refund A-1001 40.00"]),
    "expected only the first lookup (normalized) and the small refund to reach a tool");
Check(
    chains.SequenceEqual(
    [
        "audit -> policy -> normalize -> cache (miss) -> inventory.lookup ran",
        "audit -> policy -> normalize -> cache (hit)",
        "audit -> policy -> normalize -> cache (skip) -> billing.refund ran",
        "audit -> policy",
        "",
        "",
    ]),
    "expected the middleware in registration order, a cache hit short-circuiting, a denial stopping the chain");
Check(
    audit.Lines.SequenceEqual(
    [
        "billing-agent call_1 inventory.lookup: ok",
        "billing-agent call_2 inventory.lookup: ok",
        "billing-agent call_3 billing.refund: ok",
        "billing-agent call_4 billing.refund: denied (tenant acme may refund at most 100.00 without a human)",
        "billing-agent call_5 inventory.reserve: refused (not permitted)",
        "billing-agent call_6 inventory.restock: refused (no such tool)",
    ]),
    "expected the audit log to see every call");
Check(
    events.OfType<ToolCallCompleted>().Select(c => c.Result).SequenceEqual(
    [
        ToolCallResult.Succeeded, ToolCallResult.Succeeded, ToolCallResult.Succeeded,
        ToolCallResult.Refused, ToolCallResult.Refused, ToolCallResult.Refused,
    ]),
    "expected a short-circuit to be a success and a denial to be a refusal");

Console.WriteLine();
Console.WriteLine("=== Done: one registry, a subset per agent, a context per call, and middleware around every call. ===");

static ToolRegistry NewRegistry(Backend backend) =>
    new ToolRegistry()
        .Register(new InventoryTools(backend))
        .Register(new BillingTools(backend))
        .Register(new DiagnosticsTools(backend));

// What a tool-call loop tells the model, for each way a call can end.
static async Task<string> Tell(ToolRegistry tools, string tool, string arguments, ToolCallContext call, CancellationToken ct)
{
    try
    {
        return Result(await tools.DispatchToolAsync(tool, arguments, call, ct));
    }
    catch (ToolCallDeniedException denied)
    {
        return $"denied: {denied.Reason}";
    }
    catch (ToolNotPermittedException)
    {
        return "not permitted for this agent";
    }
    catch (ToolNotFoundException)
    {
        return "no such tool";
    }
}

static IEnumerable<string> ParameterNames(ToolDescriptor tool) =>
    tool.Parameters.GetProperty("properties").EnumerateObject().Select(p => p.Name);

// A generated tool returns {"result": ...}.
static string Result(string json) => JsonDocument.Parse(json).RootElement.GetProperty("result").ToString();

static async Task<Exception> Refusal(Func<Task<string>> call)
{
    try
    {
        await call();
    }
    catch (ToolNotFoundException refusal)
    {
        return refusal;
    }

    throw new InvalidOperationException("expected the call to be refused");
}

static async Task<List<RunEvent>> Drain(RunEventChannel channel)
{
    var events = new List<RunEvent>();
    await foreach (var e in channel.Reader.ReadAllAsync())
    {
        events.Add(e);
    }

    return events;
}

static string Describe(RunEvent e) => e switch
{
    ToolCallStarted s => $"ToolCallStarted   {e.NodePath}#{e.Attempt} {s.Tool} by {s.CallerId}",
    ToolCallCompleted c => $"ToolCallCompleted {e.NodePath}#{e.Attempt} {c.Tool} {c.Result}",
    _ => e.GetType().Name,
};

static bool IsUuidV7(string? id) => Guid.TryParse(id, out var guid) && guid.Version == 7;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Ticket(string Id);
