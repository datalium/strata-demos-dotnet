using System.Collections.Concurrent;
using Datalium.Strata.Tools;

namespace Datalium.Strata.Demos.ToolRegistryAndMiddleware;

/// <summary>
/// Stands in for the systems the tools talk to. It records every call that actually reached a tool,
/// so the demo can tell a call the tool ran from one a middleware or the registry stopped.
/// </summary>
public sealed class Backend
{
    private readonly ConcurrentQueue<string> _calls = new();

    /// <summary>The last context a tool was handed, as the tool saw it.</summary>
    public ToolCallContext? LastContext { get; private set; }

    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>Where a call that reached a tool is marked, after the middleware it went through.</summary>
    public ChainTrace? Trace { get; set; }

    public int CallsTo(string tool) => _calls.Count(c => c.StartsWith(tool + " ", StringComparison.Ordinal));

    public void Record(string tool, string detail, ToolCallContext? context = null)
    {
        _calls.Enqueue($"{tool} {detail}");
        Trace?.Enter($"{tool} ran");
        if (context is not null)
        {
            LastContext = context;
        }
    }
}

/// <summary>Stock lookups and reservations. A provider with a constructor dependency: tools may be instance methods.</summary>
public partial class InventoryTools(Backend backend)
{
    private static readonly Dictionary<string, int> Stock = new(StringComparer.Ordinal) { ["AB-12"] = 7, ["CD-34"] = 0 };

    [StrataTool(Name = "inventory.lookup", Description = "Units in stock for a SKU.")]
    public int Lookup(string sku)
    {
        backend.Record("inventory.lookup", sku);
        return Stock.GetValueOrDefault(sku);
    }

    // The context and the token can sit anywhere among the JSON parameters. Neither is in the
    // manifest: a model only ever sees `sku` and `count`.
    [StrataTool(Name = "inventory.reserve", Description = "Reserves units of a SKU for the calling tenant.")]
    public async Task<string> ReserveAsync(string sku, ToolCallContext context, int count, CancellationToken cancellationToken)
    {
        backend.Record("inventory.reserve", $"{count} x {sku}", context);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var tenant = context.Metadata.GetValueOrDefault("tenant", "(no tenant)");
        return $"reserved {count} x {sku} for {tenant}, requested by {context.CallerId}";
    }
}

/// <summary>Refunds.</summary>
public partial class BillingTools(Backend backend)
{
    [StrataTool(Name = "billing.refund", Description = "Refunds part or all of an order.")]
    public string Refund(string orderId, decimal amount)
    {
        backend.Record("billing.refund", FormattableString.Invariant($"{orderId} {amount:0.00}"));
        return FormattableString.Invariant($"refunded {amount:0.00} on {orderId}");
    }
}

/// <summary>A tool that only reports what it was told about its caller.</summary>
public partial class DiagnosticsTools(Backend backend)
{
    [StrataTool(Name = "diag.whoami", Description = "Reports what the tool knows about its caller.")]
    public string WhoAmI(ToolCallContext context)
    {
        backend.Record("diag.whoami", "-", context);
        return $"{context.CallerId} at {context.NodePath} (attempt {context.Attempt})";
    }
}

/// <summary>
/// An older inventory provider that also declares <c>inventory.lookup</c>. Registering it next to
/// <see cref="InventoryTools"/> is refused. Inside one class the generator's <c>STRATA003</c> already
/// catches a duplicate at compile time; across providers only the registry can.
/// </summary>
public partial class LegacyInventoryTools
{
    [StrataTool(Name = "inventory.audit", Description = "Lists stock discrepancies.")]
    public static string Audit() => "no discrepancies";

    [StrataTool(Name = "inventory.lookup", Description = "Units in stock (legacy warehouse).")]
    public static int Lookup(string sku) => 0;
}
