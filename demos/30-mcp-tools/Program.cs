// MCP tools as Strata tools (design/50-MCP-Client.md), with Datalium.Strata.Mcp.
//
// An MCP server offers tools to any client that speaks the Model Context Protocol. Strata's own MCP client connects to
// one and makes its tools ordinary Strata tools: they sit in a ToolRegistry next to [StrataTool] tools, go through the
// same middleware, and a graph node calls them the way it calls any other tool.
//
// The program has two roles. Started with --mcp-server it is a small MCP server (WarehouseServer.cs). Otherwise it is
// the client, and starts itself as that server over stdio, so the demo needs no Node, no network and no MCP package:
//
//   1. Connect      -- McpClient.ConnectAsync(McpServer.Stdio(...)): the server is probed, speaks 2026-07-28, and
//                      lists three tools.
//   2. One registry -- client.ToolProvider(...) with Include (leave the destructive tool out) and Prefix, registered
//                      next to a local [StrataTool].
//   3. Middleware   -- one audit log wraps every call, local or MCP.
//   4. A graph      -- a node makes a fixed list of tool calls, as a model's tool loop would. A tool's own error
//                      (isError) arrives as {"error": "..."} and the node goes on.
//   5. Close        -- disposing the client closes the server's stdin, and the server stops.
//
// No LLM: the "agent" is a node with a script of calls, as in demo 26.

using System.Text;
using System.Text.Json;
using Datalium.Strata.Demos.McpTools;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Mcp;
using Datalium.Strata.Storage;
using Datalium.Strata.Tools;

if (args.Contains("--mcp-server"))
{
    return await WarehouseServer.RunAsync();
}

Console.OutputEncoding = Encoding.UTF8;

// --- 1. Connect. ---

Console.WriteLine("=== 1. Connect: the demo starts itself as an MCP server, over stdio ===");
var log = new List<McpLogEntry>();
var client = await McpClient.ConnectAsync(
    ThisProgramAsServer(),
    new McpClientOptions { Log = entry => { lock (log) { log.Add(entry); } } });
Console.WriteLine($"  Protocol version: {client.ProtocolVersion}");
Console.WriteLine($"  Server:           {client.ServerInfo?.Name} {client.ServerInfo?.Version}");
Console.WriteLine("  The server's tools:");
foreach (var tool in client.Tools)
{
    Console.WriteLine($"    {tool.Name,-13} {tool.Description}{Hints(tool)}");
}

Check(client.ProtocolVersion == "2026-07-28", "expected the stateless 2026-07-28 revision");
Check(client.Tools.Select(t => t.Name).SequenceEqual(["lookup_stock", "reserve", "delete_sku"]), "expected three tools");

// --- 2. One registry. ---

Console.WriteLine();
Console.WriteLine("=== 2. One registry: a local [StrataTool] and the server's tools, side by side ===");
var registry = new ToolRegistry()
    .Register(new QuoteTools())
    .Register(client.ToolProvider(new McpToolOptions { Include = ["lookup_stock", "reserve"], Prefix = "warehouse." }));
Console.WriteLine($"  Registered: {string.Join(", ", registry.Tools.Select(t => t.Name))}");
var reserveParameters = registry.Tools.Single(t => t.Name == "warehouse.reserve")
    .Parameters.GetProperty("properties").EnumerateObject().Select(p => p.Name);
Console.WriteLine($"  warehouse.reserve takes ({string.Join(", ", reserveParameters)}), from the server's input schema.");
try
{
    await registry.DispatchToolAsync("warehouse.delete_sku", """{"sku":"A-100"}""");
    Check(false, "expected warehouse.delete_sku to be missing");
}
catch (ToolNotFoundException)
{
    Console.WriteLine("  warehouse.delete_sku: no such tool. Include left it out, so nothing that uses this registry can call it.");
}

Check(
    registry.Tools.Select(t => t.Name).SequenceEqual(["quote", "warehouse.lookup_stock", "warehouse.reserve"]),
    "expected the local tool, then the two MCP tools with the prefix");

// --- 3. Middleware. ---

Console.WriteLine();
Console.WriteLine("=== 3. Middleware: one audit log around every call, local or MCP ===");
var audit = new AuditLog();
registry.Use(audit);
await registry.DispatchToolAsync("quote", """{"sku":"A-100","quantity":1}""");
await registry.DispatchToolAsync("warehouse.lookup_stock", """{"sku":"A-100"}""");
Console.WriteLine("  A [StrataTool] returns {\"result\": ...}. An MCP tool returns its structured content as it is.");
Check(audit.Lines.Count == 4, "expected the audit log to see both calls");

// --- 4. A graph whose node calls the tools. ---

Console.WriteLine();
Console.WriteLine("=== 4. A graph: a node calls the tools, as a model's tool loop would ===");
(string Tool, string Arguments)[] script =
[
    ("warehouse.lookup_stock", """{"sku":"A-100"}"""),
    ("quote", """{"sku":"A-100","quantity":3}"""),
    ("warehouse.reserve", """{"sku":"A-100","quantity":3}"""),
    ("warehouse.reserve", """{"sku":"B-200","quantity":1}"""),
];
var graph = new StateGraph<Order>()
    .AddNode("Fulfil", async (Order order, NodeContext ctx, CancellationToken ct) =>
    {
        var call = ToolCallContext.From(ctx) with { CallerId = "order-agent" };
        foreach (var (tool, arguments) in script)
        {
            var result = JsonDocument.Parse(await registry.DispatchToolAsync(tool, arguments, call, ct)).RootElement;
            if (tool != "warehouse.reserve")
            {
                continue;
            }

            var line = JsonDocument.Parse(arguments).RootElement;
            var what = $"{line.GetProperty("quantity").GetInt32()} x {line.GetProperty("sku").GetString()}";
            // The tool's own error is a result, not an exception: the node reads it and goes on.
            order = result.TryGetProperty("error", out var error)
                ? order with { Backordered = [.. order.Backordered, $"{what} ({error.GetString()})"] }
                : order with { Reserved = [.. order.Reserved, $"{what} ({result.GetProperty("left").GetInt32()} left)"] };
        }

        return order;
    })
    .Compile();
var run = await graph.RunAsync("order-1001", new Order("order-1001", [], []), new InMemoryCheckpointStore());
Check(run is GraphExecutionResult<Order>.Completed, $"expected the run to complete, got {run}");
var final = ((GraphExecutionResult<Order>.Completed)run).State;
Console.WriteLine($"  Final state of {final.Id}:");
Console.WriteLine($"    reserved:    {string.Join("; ", final.Reserved)}");
Console.WriteLine($"    backordered: {string.Join("; ", final.Backordered)}");
Check(final.Reserved.SequenceEqual(["3 x A-100 (9 left)"]), "expected the A-100 line reserved");
Check(final.Backordered.SequenceEqual(["1 x B-200 (only 0 of B-200 on hand)"]), "expected the B-200 line backordered");

// --- 5. Close. ---

Console.WriteLine();
Console.WriteLine("=== 5. Close: the client stops the server ===");
await client.DisposeAsync();
string[] stderr;
lock (log)
{
    stderr = [.. log.Where(e => e.Message.StartsWith("stderr: ", StringComparison.Ordinal)).Select(e => e.Message["stderr: ".Length..])];
}

Console.WriteLine("  Disposing the client closed the server's stdin. The server's last words, on its stderr,");
Console.WriteLine("  which the client reads and reports to its log:");
foreach (var line in stderr)
{
    Console.WriteLine($"    {line}");
}

Check(stderr.SequenceEqual(["warehouse: stopped after 4 tool calls"]), "expected the server to stop after four calls");
Console.WriteLine("  Four calls reached the server: warehouse.delete_sku never did.");

Console.WriteLine();
Console.WriteLine("=== Done: an MCP server's tools, registered, filtered, wrapped in middleware and called from a graph. ===");
return 0;

// This program, started again in its server role. `dotnet run` starts the apphost, so ProcessPath is this program;
// started as `dotnet 30-mcp-tools.dll`, it is the dotnet host, which needs the assembly's path first.
static StdioMcpServer ThisProgramAsServer()
{
    var self = Environment.ProcessPath!;
    return Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        ? McpServer.Stdio(self, [typeof(WarehouseServer).Assembly.Location, "--mcp-server"])
        : McpServer.Stdio(self, ["--mcp-server"]);
}

// A tool's annotations are the server's hints, not guarantees: the client decides what to expose.
static string Hints(McpTool tool) =>
    tool.Annotations is { ValueKind: JsonValueKind.Object } annotations
        ? $" ({string.Join(", ", annotations.EnumerateObject().Where(a => a.Value.ValueKind == JsonValueKind.True).Select(a => a.Name))})"
        : string.Empty;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

/// <summary>The order the graph fulfils: what it could reserve, and what it couldn't.</summary>
sealed record Order(string Id, IReadOnlyList<string> Reserved, IReadOnlyList<string> Backordered);
