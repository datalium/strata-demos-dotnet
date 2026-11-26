using System.Text;
using System.Text.Json.Nodes;

namespace Datalium.Strata.Demos.McpTools;

/// <summary>
/// The demo's own MCP server, run when the program is started with <c>--mcp-server</c>. It is written by hand, with no
/// MCP package, to show how little a client needs from the other side: MCP's stdio transport is one JSON-RPC 2.0
/// message per line, on stdin and stdout, and nothing else on stdout. It speaks the stateless <c>2026-07-28</c> revision:
/// <c>server/discover</c>, <c>tools/list</c> and <c>tools/call</c>. Any other request gets "method not found", and
/// notifications get no answer. Diagnostics go to stderr, which the client reads and reports to its log.
/// </summary>
public static class WarehouseServer
{
    private const string Version = "2026-07-28";

    // What the warehouse holds: name and units on hand, per SKU.
    private static readonly Dictionary<string, (string Name, int OnHand)> Stock = new(StringComparer.Ordinal)
    {
        ["A-100"] = ("Widget", 12),
        ["B-200"] = ("Gadget", 0),
    };

    private static readonly JsonNode Tools = JsonNode.Parse("""
        [
          {
            "name": "lookup_stock",
            "description": "Units on hand for a SKU.",
            "inputSchema": {
              "type": "object",
              "properties": { "sku": { "type": "string", "description": "The SKU, such as A-100." } },
              "required": ["sku"]
            },
            "annotations": { "readOnlyHint": true }
          },
          {
            "name": "reserve",
            "description": "Reserves units of a SKU for an order.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "sku": { "type": "string", "description": "The SKU, such as A-100." },
                "quantity": { "type": "integer", "minimum": 1, "description": "How many units." }
              },
              "required": ["sku", "quantity"]
            }
          },
          {
            "name": "delete_sku",
            "description": "Deletes a SKU and its stock.",
            "inputSchema": {
              "type": "object",
              "properties": { "sku": { "type": "string" } },
              "required": ["sku"]
            },
            "annotations": { "destructiveHint": true }
          }
        ]
        """)!;

    public static async Task<int> RunAsync()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        var calls = 0;
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var request = JsonNode.Parse(line)!;
            if (request["id"] is not { } id)
            {
                continue; // A notification: no answer.
            }

            var method = request["method"]?.GetValue<string>() ?? string.Empty;
            var tool = method == "tools/call" ? request["params"]?["name"]?.GetValue<string>() : null;
            JsonObject response = new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            switch (method)
            {
                case "server/discover":
                    response["result"] = new JsonObject
                    {
                        ["resultType"] = "complete",
                        ["supportedVersions"] = new JsonArray(Version),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["_meta"] = new JsonObject
                        {
                            ["io.modelcontextprotocol/serverInfo"] = new JsonObject { ["name"] = "warehouse", ["version"] = "1.0.0" },
                        },
                    };
                    break;
                case "tools/list":
                    response["result"] = new JsonObject { ["resultType"] = "complete", ["tools"] = Tools.DeepClone() };
                    break;
                case "tools/call" when tool is "lookup_stock" or "reserve" or "delete_sku":
                    calls++;
                    response["result"] = Call(tool, request["params"]?["arguments"]);
                    break;
                case "tools/call":
                    // MCP: an unknown tool is a protocol error, not a tool error.
                    response["error"] = new JsonObject { ["code"] = -32602, ["message"] = "unknown tool" };
                    break;
                default:
                    response["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {method}" };
                    break;
            }

            await writer.WriteLineAsync(response.ToJsonString());
        }

        // stdin closed: the client is done with us (MCP's stdio shutdown).
        await Console.Error.WriteLineAsync($"warehouse: stopped after {calls} tool calls");
        return 0;
    }

    private static JsonObject Call(string tool, JsonNode? arguments)
    {
        var sku = arguments?["sku"]?.GetValue<string>() ?? string.Empty;
        if (!Stock.TryGetValue(sku, out var item))
        {
            return Error($"no such SKU: {sku}");
        }

        // Structured content keeps its keys in alphabetical order, the order the Rust twin prints them in
        // (serde_json sorts an object's keys), so both demos print the same lines.
        switch (tool)
        {
            case "lookup_stock":
                return Success(
                    $"{sku} {item.Name}: {item.OnHand} on hand",
                    new JsonObject { ["name"] = item.Name, ["on_hand"] = item.OnHand, ["sku"] = sku });
            case "reserve":
                var quantity = arguments?["quantity"]?.GetValue<int>() ?? 0;
                if (quantity < 1)
                {
                    return Error("quantity must be at least 1");
                }

                if (quantity > item.OnHand)
                {
                    return Error($"only {item.OnHand} of {sku} on hand");
                }

                Stock[sku] = item with { OnHand = item.OnHand - quantity };
                return Success(
                    $"reserved {quantity} of {sku}",
                    new JsonObject { ["left"] = item.OnHand - quantity, ["reserved"] = quantity, ["sku"] = sku });
            default: // delete_sku
                Stock.Remove(sku);
                return Success($"deleted {sku}", new JsonObject { ["deleted"] = true, ["sku"] = sku });
        }
    }

    // A tool result: a text block for any client, and the same facts as structured content.
    private static JsonObject Success(string text, JsonObject structured) => new()
    {
        ["resultType"] = "complete",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["structuredContent"] = structured,
    };

    // The tool's own error: a result with isError, not a JSON-RPC error, so the model can read it and try again.
    private static JsonObject Error(string message) => new()
    {
        ["resultType"] = "complete",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };
}
