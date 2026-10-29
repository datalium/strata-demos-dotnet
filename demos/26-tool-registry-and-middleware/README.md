# 26 — tool-registry-and-middleware

The tool registry, the tool call context and tool middleware
(`design/20-Tool-Dispatch-And-Registry.md`, `design/23-Tool-Middleware.md`).
[`02-tool-calling`](../02-tool-calling) dispatches one generated provider
directly. A real agent platform has several providers, and each agent should
see only some of their tools. This demo puts three `[StrataTool]` providers
(inventory, billing, diagnostics) into one `ToolRegistry`, gives each agent a
subset, lets a tool see who called it, and wraps every call in middleware: an
audit log, a refund policy, an argument rewrite and a cache.

No LLM: the "agent" is a graph node that makes a fixed list of tool calls, the
way a model's tool-call loop would. The Rust twin is
[`strata-demos-rust/demos/26-tool-registry-and-middleware`](https://github.com/datalium/strata-demos-rust/tree/main/demos/26-tool-registry-and-middleware);
its README says where the two engines differ.

## What it demonstrates

- **One registry, many providers.** `new ToolRegistry().Register(...)` merges
  the providers' manifests in registration order (`registry.Tools`, each a
  `ToolDescriptor` with its JSON-Schema parameters). Registering a provider
  that declares a name already taken throws `DuplicateToolException`, and
  *none* of that provider's tools are added (its unique `inventory.audit` is
  missing too). Two `[StrataTool]` methods with one name in the *same* class
  are caught earlier, at compile time, by the generator's `STRATA003`; across
  providers only the registry can see the clash
- **Per-agent subsets.** `registry.Subset(names)` keeps the registry's order,
  not the order asked for. A tool the subset left out throws
  `ToolNotPermittedException` ("not permitted here"), which is distinct from
  a name nobody registered (`ToolNotFoundException`, "not found"). The first
  derives from the second, so a catch that tells a model "no such tool"
  handles both; catch `ToolNotPermittedException` first to tell them apart.
  A subset can only narrow: asking it for a tool it left out is also "not
  permitted"
- **The tool sees its caller.** `ToolCallContext.From(nodeContext) with {
  CallerId = ... }` copies the thread id, run id, qualified node path
  (`Triage/Investigate`, inside a subgraph), attempt and run metadata
  (`tenant`, `ticket`). When the caller sets no `CallId`, the registry
  generates a UUID v7. A `[StrataTool]` method receives the context and the
  token by declaring `ToolCallContext` and `CancellationToken` parameters, in
  any position; neither appears in the manifest (`inventory.reserve(sku,
  ToolCallContext, count, CancellationToken)` is listed as `(sku, count)`)
- **A cancelled token stops a call before it starts.** The tool never runs
  (nor would any middleware), and the call is reported `Cancelled`
- **`ToolCallStarted` / `ToolCallCompleted` run events** for every call whose
  context came from a node, at the calling node's path and attempt, with the
  tool, call id, caller and result. They don't carry the arguments or the
  result, which can hold secrets
- **Middleware with `Use(IToolMiddleware)`**, run in registration order,
  outermost first. The demo prints the chain each call took:
  - `AuditLog` (outermost) records every call's outcome, including the ones
    a middleware inside it denied or answered from the cache
  - `RefundPolicy` reads the tenant from the call's metadata and throws
    `ToolCallDeniedException` above the tenant's limit. The call is reported
    `Refused`, with the reason, and the tool never runs
  - `NormalizeSku` rewrites the arguments (`call with { Arguments = ... }`):
    `" ab-12 "` becomes `"AB-12"`
  - `ReadCache` answers a repeated lookup without calling `next` (a
    short-circuit, reported `Succeeded`). Because it is registered after
    `NormalizeSku`, `" ab-12 "` and `"AB-12"` share one cache entry: order
    matters. It also keys by tenant, so one tenant's answer is never served
    to another
- **Refused calls skip the chain.** A call the registry refuses (not
  permitted, not found) runs no `InvokeAsync`; each middleware's
  `OnRefusedAsync` hook sees it instead, which is how the audit log records
  those two
- **A subset made after `Use(...)` shares the parent's chain**, so the billing
  agent's calls go through the shared audit and policy

## What it does *not* cover

- **The model ↔ tool loop.** Here a node calls tools from a fixed script. See
  [`23-agent-tool-loop`](../23-agent-tool-loop) for `StrataAgent` driving a
  registry from a (scripted) model's tool calls
- **The `strata.tool` span and the `strata.tool.*` metrics.** Every registry
  dispatch has them (a cache hit is tagged `strata.tool.short_circuited`), but
  this demo reads only the run events. See
  [`27-run-observability`](../27-run-observability) for listening to the
  engine's spans and metrics
- **A middleware that calls `next` more than once, a timeout middleware, or a
  failing middleware** (reported `Failed`, not `Refused`). The engine's
  `ToolMiddlewareTests.cs` covers them
- **A registry registered into another**, which keeps its own middleware on
  its tools
- **Real policy.** The refund limit is a dictionary. Authorization rules, rate
  limits and audit storage belong to whatever sits behind the middleware

## Run it

```bash
dotnet run --project demos/26-tool-registry-and-middleware/26-tool-registry-and-middleware.csproj
```

Expected output. Run ids and generated call ids are UUID v7s, different on
every run, so the demo checks their version and prints a placeholder:

```
=== Step 1: one registry, three [StrataTool] providers ===
  Registered, in registration order (parameters from the merged manifest):
    inventory.lookup(sku)
    inventory.reserve(sku, count)
    billing.refund(orderId, amount)
    diag.whoami()
  inventory.reserve takes (sku, ToolCallContext, count, CancellationToken); its manifest lists only (sku, count).
  Registering LegacyInventoryTools: refused -- Tool 'inventory.lookup' is already registered.
  Its other tool, inventory.audit, was not registered either: 4 tools, none of them inventory.audit.

=== Step 2: a per-agent Subset(...) ===
  support-agent's subset, asked for in another order: inventory.lookup, inventory.reserve, diag.whoami
  inventory.lookup AB-12 -> 7
  billing.refund   -> ToolNotPermittedException: Tool 'billing.refund' is not permitted here.
  inventory.restock -> ToolNotFoundException: Tool 'inventory.restock' was not found.
  support.Subset("billing.refund") -> ToolNotPermittedException: a subset can only narrow.
  ToolNotPermittedException derives from ToolNotFoundException, so code that tells a model "no such tool" covers both.

=== Step 3: ToolCallContext.From(nodeContext) -- the tool sees its caller ===
  What inventory.reserve was handed:
    ThreadId:  ticket-T-1001
    RunId:     (a UUID v7, generated per run)
    NodePath:  Triage/Investigate
    Attempt:   1
    Metadata:  tenant=acme, ticket=T-1001
    CallerId:  support-agent
    CallId:    (a UUID v7, generated by the registry)
  Results:
    support-agent at Triage/Investigate (attempt 1)
    reserved 2 x AB-12 for acme, requested by support-agent
    cancelled before the tool ran
  Tool-call run events (the arguments and results are not in them -- they can hold secrets):
    #4  ToolCallStarted   Triage/Investigate#1 diag.whoami by support-agent
    #5  ToolCallCompleted Triage/Investigate#1 diag.whoami Succeeded
    #6  ToolCallStarted   Triage/Investigate#1 inventory.reserve by support-agent
    #7  ToolCallCompleted Triage/Investigate#1 inventory.reserve Succeeded
    #8  ToolCallStarted   Triage/Investigate#1 inventory.reserve by support-agent
    #9  ToolCallCompleted Triage/Investigate#1 inventory.reserve Cancelled

=== Step 4: middleware -- audit, policy, rewrite, cache ===
  What the agent was told for each call (what a model would see):
    call_1 inventory.lookup {"sku":" ab-12 "} -> 7
    call_2 inventory.lookup {"sku":"AB-12"} -> 7
    call_3 billing.refund {"orderId":"A-1001","amount":40} -> refunded 40.00 on A-1001
    call_4 billing.refund {"orderId":"A-1002","amount":250} -> denied: tenant acme may refund at most 100.00 without a human
    call_5 inventory.reserve {"sku":"AB-12","count":1} -> not permitted for this agent
    call_6 inventory.restock {"sku":"AB-12"} -> no such tool
  The chain each call took (registration order, outermost first; refused calls skip it):
    call_1: audit -> policy -> normalize -> cache (miss) -> inventory.lookup ran
    call_2: audit -> policy -> normalize -> cache (hit)
    call_3: audit -> policy -> normalize -> cache (skip) -> billing.refund ran
    call_4: audit -> policy
    call_5: (none)
    call_6: (none)
  Audit log (outermost, so it sees denials, cache hits and refusals too):
    billing-agent call_1 inventory.lookup: ok
    billing-agent call_2 inventory.lookup: ok
    billing-agent call_3 billing.refund: ok
    billing-agent call_4 billing.refund: denied (tenant acme may refund at most 100.00 without a human)
    billing-agent call_5 inventory.reserve: refused (not permitted)
    billing-agent call_6 inventory.restock: refused (no such tool)
  Calls that reached a tool: inventory.lookup AB-12; billing.refund A-1001 40.00
  ToolCallCompleted events:
    call_1 inventory.lookup: Succeeded
    call_2 inventory.lookup: Succeeded
    call_3 billing.refund: Succeeded
    call_4 billing.refund: Refused -- Tool 'billing.refund' was denied: tenant acme may refund at most 100.00 without a human
    call_5 inventory.reserve: Refused -- Tool 'inventory.reserve' is not permitted here.
    call_6 inventory.restock: Refused -- Tool 'inventory.restock' was not found.

=== Done: one registry, a subset per agent, a context per call, and middleware around every call. ===
```

The demo checks every claim above (registration order, the refused provider,
the subset's order and refusals, the context the tool saw, the event results,
the chain each call took, the audit log and which calls reached a tool) and
exits non-zero if one fails.

## Files

- `Program.cs` — the four steps, top-level statements
- `Tools.cs` — the `[StrataTool]` providers (`InventoryTools`, `BillingTools`,
  `DiagnosticsTools`, and the clashing `LegacyInventoryTools`) and the
  `Backend` that records which calls reached a tool
- `Middleware.cs` — `AuditLog`, `RefundPolicy`, `NormalizeSku`, `ReadCache`,
  and the `ChainTrace` they write the chain into
- `26-tool-registry-and-middleware.csproj` — references `Datalium.Strata` and
  `Datalium.Strata.Storage` from NuGet; the `[StrataTool]` source generator
  ships inside `Datalium.Strata`
