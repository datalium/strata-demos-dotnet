# 30 — mcp-tools

An MCP server's tools as Strata tools, with Strata's own MCP client,
`Datalium.Strata.Mcp` (`design/50-MCP-Client.md` in
[`datalium/strata`](https://github.com/datalium/strata)). An MCP server offers
tools to any client that speaks the Model Context Protocol. The client connects
to one and makes its tools ordinary Strata tools: they sit in a `ToolRegistry`
next to `[StrataTool]` tools, go through the same middleware, and a graph node
calls them the way it calls any other tool.

**Needs Strata `1.0.0-rc.1`.** `Datalium.Strata.Mcp` is new in that release.

**The server is this program.** Started with `--mcp-server`, the demo is a
small MCP server, `warehouse` (`WarehouseServer.cs`), written by hand with no
MCP package. Otherwise it is the client, and starts itself as that server over
stdio. So the demo runs offline, with no Node, no network and no API key.

No LLM: the "agent" is a graph node that makes a fixed list of tool calls, the
way a model's tool loop would, as in
[`26-tool-registry-and-middleware`](../26-tool-registry-and-middleware). The
Rust twin is
[`strata-demos-rust/demos/30-mcp-tools`](https://github.com/datalium/strata-demos-rust/tree/main/demos/30-mcp-tools).

## What it demonstrates

- **Connecting** (section 1). `McpClient.ConnectAsync(McpServer.Stdio(...))`
  starts the server as a child process, probes it with `server/discover`,
  agrees on the stateless `2026-07-28` revision, and lists its tools. The
  client then has `ProtocolVersion`, `ServerInfo` and `Tools`, each tool with
  its description, input schema and annotations.
- **One registry** (section 2). `client.ToolProvider(...)` makes the server's
  tools a provider, registered next to a local `[StrataTool]` provider
  (`quote`). `Include` picks the tools to expose and leaves out the
  destructive `delete_sku`: it isn't in the registry, so nothing that uses the
  registry can call it. A tool's annotations (`destructiveHint`) are the
  server's hints, not guarantees, so the client decides. `Prefix` names each
  server's tools (`warehouse.lookup_stock`), so two servers can't clash. A
  tool's parameters are the server's input schema, unchanged.
- **Middleware** (section 3). One audit log, added with `Use(...)`, wraps every
  call. It doesn't know which tools are local and which run on the server.
  A `[StrataTool]` returns `{"result": ...}`; an MCP tool returns its
  structured content as it is.
- **A graph** (section 4). A node makes four calls through the registry with
  `ToolCallContext.From(ctx)`. Reserving `B-200`, which has none on hand, makes
  the tool report an error (`isError`). That reaches the node as
  `{"error": "only 0 of B-200 on hand"}`, a result and not an exception, so
  the node goes on and marks the line backordered, as a model would read the
  error and answer.
- **Closing** (section 5). Disposing the client closes the server's stdin,
  which is how MCP's stdio transport ends a session. The server writes its
  last line to stderr and exits. The client reads a server's stderr all the
  time and reports it to `McpClientOptions.Log` (at `Debug`, and never on a
  span, because stderr can hold secrets), so the demo shows that line: four
  calls reached the server, and `delete_sku` was never one of them.

## What it does *not* cover

- **Streamable HTTP.** `McpServer.Http(new Uri(...))` connects to a server over
  HTTP, with headers or an `AccessToken` hook for a bearer token. This demo
  uses stdio only
- **Older servers.** A server that doesn't answer `server/discover` gets the
  `initialize` handshake (versions back to `2025-03-26`, and `2024-11-05` on
  stdio). The engine's tests cover both kinds of server
- **A model.** Here a node calls tools from a script. See
  [`23-agent-tool-loop`](../23-agent-tool-loop) for `StrataAgent` driving a
  registry from a (scripted) model's tool calls; the same agent can use MCP
  tools
- **What isn't built:** Strata as an MCP server, resources, prompts,
  sampling, roots, elicitation and full OAuth 2.1
- **The `mcp.request` span and the `mcp.client.operation.duration`
  histogram.** Every request has them. See
  [`27-run-observability`](../27-run-observability) for listening to spans
  and metrics

## Run it

```bash
dotnet run --project demos/30-mcp-tools/30-mcp-tools.csproj
```

Expected output:

```
=== 1. Connect: the demo starts itself as an MCP server, over stdio ===
  Protocol version: 2026-07-28
  Server:           warehouse 1.0.0
  The server's tools:
    lookup_stock  Units on hand for a SKU. (readOnlyHint)
    reserve       Reserves units of a SKU for an order.
    delete_sku    Deletes a SKU and its stock. (destructiveHint)

=== 2. One registry: a local [StrataTool] and the server's tools, side by side ===
  Registered: quote, warehouse.lookup_stock, warehouse.reserve
  warehouse.reserve takes (sku, quantity), from the server's input schema.
  warehouse.delete_sku: no such tool. Include left it out, so nothing that uses this registry can call it.

=== 3. Middleware: one audit log around every call, local or MCP ===
    → quote {"sku":"A-100","quantity":1}
    ← {"result":{"sku":"A-100","quantity":1,"total":2.5}}
    → warehouse.lookup_stock {"sku":"A-100"}
    ← {"sku":"A-100","name":"Widget","on_hand":12}
  A [StrataTool] returns {"result": ...}. An MCP tool returns its structured content as it is.

=== 4. A graph: a node calls the tools, as a model's tool loop would ===
    → warehouse.lookup_stock {"sku":"A-100"}
    ← {"sku":"A-100","name":"Widget","on_hand":12}
    → quote {"sku":"A-100","quantity":3}
    ← {"result":{"sku":"A-100","quantity":3,"total":7.5}}
    → warehouse.reserve {"sku":"A-100","quantity":3}
    ← {"sku":"A-100","reserved":3,"left":9}
    → warehouse.reserve {"sku":"B-200","quantity":1}
    ← {"error":"only 0 of B-200 on hand"}
  Final state of order-1001:
    reserved:    3 x A-100 (9 left)
    backordered: 1 x B-200 (only 0 of B-200 on hand)

=== 5. Close: the client stops the server ===
  Disposing the client closed the server's stdin. The server's last words, on its stderr,
  which the client reads and reports to its log:
    warehouse: stopped after 4 tool calls
  Four calls reached the server: warehouse.delete_sku never did.

=== Done: an MCP server's tools, registered, filtered, wrapped in middleware and called from a graph. ===
```

The demo checks every claim above (the protocol version, the tools, the
registry's names, the left-out tool, the audit log, the final state and the
server's last line) and exits non-zero if one fails.

## Files

- `Program.cs` — the client: the five sections, top-level statements
- `WarehouseServer.cs` — the MCP server, run with `--mcp-server`: one JSON-RPC
  message per line on stdin and stdout, `server/discover`, `tools/list` and
  `tools/call`
- `Tools.cs` — the local `[StrataTool]` `quote` and the `AuditLog` middleware
- `30-mcp-tools.csproj` — references `Datalium.Strata`, `Datalium.Strata.Storage`
  and `Datalium.Strata.Mcp` from NuGet
