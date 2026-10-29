using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Datalium.Strata.Tools;

namespace Datalium.Strata.Demos.ToolRegistryAndMiddleware;

/// <summary>
/// The order middleware is entered in, shared by every middleware below, so the demo can print the
/// chain one call took.
/// </summary>
public sealed class ChainTrace
{
    private readonly ConcurrentQueue<string> _steps = new();

    public void Enter(string step) => _steps.Enqueue(step);

    public string Drain()
    {
        var steps = new List<string>();
        while (_steps.TryDequeue(out var step))
        {
            steps.Add(step);
        }

        return string.Join(" -> ", steps);
    }
}

/// <summary>
/// Registered first, so it is outermost: it sees every call, including the ones a middleware inside it
/// denies or answers from a cache, and, through <see cref="OnRefusedAsync"/>, the ones the registry
/// refuses before any middleware runs.
/// </summary>
public sealed class AuditLog(ChainTrace trace) : IToolMiddleware
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public async Task<string> InvokeAsync(ToolCall call, ToolCallDelegate next, CancellationToken cancellationToken)
    {
        trace.Enter("audit");
        try
        {
            var result = await next(call, cancellationToken);
            Write(call, "ok");
            return result;
        }
        catch (ToolCallDeniedException denied)
        {
            Write(call, $"denied ({denied.Reason})");
            throw;
        }
        catch (Exception ex)
        {
            Write(call, $"failed ({ex.GetType().Name})");
            throw;
        }
    }

    public Task OnRefusedAsync(ToolCall call, ToolNotFoundException refusal, CancellationToken cancellationToken)
    {
        // ToolNotPermittedException derives from ToolNotFoundException: test for it first.
        Write(call, refusal is ToolNotPermittedException ? "refused (not permitted)" : "refused (no such tool)");
        return Task.CompletedTask;
    }

    private void Write(ToolCall call, string outcome) =>
        _lines.Enqueue($"{call.Context.CallerId} {call.Context.CallId} {call.Name}: {outcome}");
}

/// <summary>
/// A refund limit per tenant. The tenant comes from the run's metadata, which reached the call through
/// <see cref="ToolCallContext.From"/>; the tool itself knows nothing about it.
/// </summary>
public sealed class RefundPolicy(ChainTrace trace, IReadOnlyDictionary<string, decimal> limits) : IToolMiddleware
{
    public Task<string> InvokeAsync(ToolCall call, ToolCallDelegate next, CancellationToken cancellationToken)
    {
        trace.Enter("policy");
        if (call.Name != "billing.refund")
        {
            return next(call, cancellationToken);
        }

        if (!call.Context.Metadata.TryGetValue("tenant", out var tenant) || !limits.TryGetValue(tenant, out var limit))
        {
            throw new ToolCallDeniedException(call.Name, "the call carries no known tenant");
        }

        var amount = JsonNode.Parse(call.Arguments)!["amount"]!.GetValue<decimal>();
        return amount > limit
            ? throw new ToolCallDeniedException(
                call.Name, FormattableString.Invariant($"tenant {tenant} may refund at most {limit:0.00} without a human"))
            : next(call, cancellationToken);
    }
}

/// <summary>Rewrites the arguments: trims and upper-cases a <c>sku</c>, so " ab-12 " and "AB-12" are the same call.</summary>
public sealed class NormalizeSku(ChainTrace trace) : IToolMiddleware
{
    public Task<string> InvokeAsync(ToolCall call, ToolCallDelegate next, CancellationToken cancellationToken)
    {
        trace.Enter("normalize");
        var arguments = JsonNode.Parse(call.Arguments)!.AsObject();
        if (arguments["sku"]?.GetValue<string>() is { } sku)
        {
            arguments["sku"] = sku.Trim().ToUpperInvariant();
            call = call with { Arguments = arguments.ToJsonString() };
        }

        return next(call, cancellationToken);
    }
}

/// <summary>
/// Answers repeated read-only calls without running the tool (a short-circuit, which the registry reports
/// as succeeded). Registered after <see cref="NormalizeSku"/>, so it sees normalized arguments, and
/// keyed by tenant, so one tenant's answer is never served to another.
/// </summary>
public sealed class ReadCache(ChainTrace trace, params string[] cacheable) : IToolMiddleware
{
    private readonly ConcurrentDictionary<string, string> _results = new(StringComparer.Ordinal);

    public async Task<string> InvokeAsync(ToolCall call, ToolCallDelegate next, CancellationToken cancellationToken)
    {
        if (!cacheable.Contains(call.Name))
        {
            trace.Enter("cache (skip)");
            return await next(call, cancellationToken);
        }

        var key = $"{call.Context.Metadata.GetValueOrDefault("tenant")}|{call.Name}|{Canonical(call.Arguments)}";
        if (_results.TryGetValue(key, out var cached))
        {
            trace.Enter("cache (hit)");
            return cached;
        }

        trace.Enter("cache (miss)");
        var result = await next(call, cancellationToken);
        _results[key] = result;
        return result;
    }

    private static string Canonical(string json) => JsonSerializer.Serialize(JsonNode.Parse(json));
}
