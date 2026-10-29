using Datalium.Strata.Tools;

namespace Datalium.Strata.Demos.AgentToolLoop;

/// <summary>An order as <c>orders.lookup</c> returns it.</summary>
public sealed record OrderInfo(string OrderId, string CustomerId, decimal Total, string Status, int DaysSinceDelivery);

/// <summary>
/// A small support desk: an order book, a shipping tracker, a refund gateway and an account service, as
/// <c>[StrataTool]</c> methods. The source generator makes this class an <see cref="IStrataToolProvider"/> with a
/// static dispatch switch. The tools are instance methods, so each scenario gets a fresh desk and can count what ran.
/// </summary>
/// <remarks>
/// The names are dotted, as Strata names usually are. Providers don't accept a dot in a tool name, which is what
/// <c>ToolDeclarations</c> is for.
/// </remarks>
public partial class SupportDesk
{
    private readonly Dictionary<string, OrderInfo> _orders = new(StringComparer.Ordinal)
    {
        ["A-1001"] = new("A-1001", "C-77", 39.90m, "delivered, box crushed in transit", 3),
        ["A-1002"] = new("A-1002", "C-77", 129.00m, "in transit", 0),
        ["A-1003"] = new("A-1003", "C-12", 64.50m, "delivered", 70),
    };

    private int _nextRefund = 1;

    /// <summary>Every tool that ran, as "name(argument)", in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Refunds actually issued: (refund id, order, amount).</summary>
    public List<(string RefundId, string OrderId, decimal Amount)> Refunds { get; } = [];

    /// <summary>When set, the refund gateway rejects every refund with this message.</summary>
    public string? GatewayFailure { get; set; }

    [StrataTool(Name = "orders.lookup", Description = "Looks an order up by its id: customer, total, status, days since delivery.")]
    public OrderInfo? Lookup(string orderId)
    {
        Calls.Add($"orders.lookup({orderId})");

        // An unknown order is an answer, not an exception: the model should hear "no such order".
        return _orders.GetValueOrDefault(orderId);
    }

    [StrataTool(Name = "shipping.track", Description = "Where an order's parcel is now.")]
    public string Track(string orderId)
    {
        Calls.Add($"shipping.track({orderId})");
        return _orders.TryGetValue(orderId, out var order) && order.Status == "in transit"
            ? "in transit: left the Rotterdam hub, no delivery date yet"
            : "delivered";
    }

    [StrataTool(Name = "refunds.issue", Description = "Refunds an amount of an order to the customer's card. Returns the refund id.")]
    public string Refund(string orderId, decimal amount, string reason)
    {
        Calls.Add($"refunds.issue({orderId}, {amount}, {reason})");
        if (GatewayFailure is not null)
        {
            // A business failure. Any exception the tool throws fails the node (or goes back to the model under
            // ToolFailures.ReturnToModel); only arguments the generated dispatch couldn't bind are refused.
            throw new InvalidOperationException(GatewayFailure);
        }

        var id = $"RF-{_nextRefund++:D4}";
        Refunds.Add((id, orderId, amount));
        return id;
    }

    [StrataTool(Name = "accounts.close", Description = "Closes a customer's account. Not for the support agent.")]
    public string CloseAccount(string customerId)
    {
        Calls.Add($"accounts.close({customerId})");
        return "closed";
    }
}
