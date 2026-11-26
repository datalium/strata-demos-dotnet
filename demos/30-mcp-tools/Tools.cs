using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Datalium.Strata.Tools;

namespace Datalium.Strata.Demos.McpTools;

/// <summary>A price quote, with the field names the MCP tools use too.</summary>
public sealed record Quote(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("total")] double Total);

/// <summary>A local tool, generated at compile time, that sits in the same registry as the MCP server's tools.</summary>
public partial class QuoteTools
{
    private static readonly Dictionary<string, double> Prices = new(StringComparer.Ordinal) { ["A-100"] = 2.50, ["B-200"] = 4.00 };

    [StrataTool(Name = "quote", Description = "Prices a quantity of a SKU.")]
    public static Quote Price(string sku, int quantity) =>
        new(sku, quantity, Prices.TryGetValue(sku, out var price) ? price * quantity : 0);
}

/// <summary>
/// One middleware around every call in the registry. It can't tell, and doesn't need to know, which tools are local and
/// which an MCP server runs: both are just tools to it.
/// </summary>
public sealed class AuditLog : IToolMiddleware
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public async Task<string> InvokeAsync(ToolCall call, ToolCallDelegate next, CancellationToken cancellationToken)
    {
        Write($"→ {call.Name} {call.Arguments}");
        var result = await next(call, cancellationToken);
        Write($"← {result}");
        return result;
    }

    private void Write(string line)
    {
        _lines.Enqueue(line);
        Console.WriteLine($"    {line}");
    }
}
