// The agent (design/44-LLM-Integration.md, "The agent's loop", "Tool names", "Structured output"):
// StrataAgent<TOutput> and AddAgentNode from Datalium.Strata.AI.
//
// A support desk's refund agent. The agent node asks the model, dispatches the tool calls it makes through a
// ToolRegistry of [StrataTool] methods (SupportDesk.cs), sends the results back, and stops when the model answers
// without calling a tool. The answer is a typed RefundDecision, parsed and validated, and sent back to be repaired
// when it is bad.
//
// The model is a stand-in (ScriptedModel.cs): an IChatClient that plays a script and records what it was sent, so
// the demo runs offline with no API key. The agent can't tell the difference. Demo 03 runs the same kind of loop
// against a real OpenAI model.
//
//   1. The loop: two tool turns (one with two calls), then a typed answer. Provider-safe names mapped back.
//   2. A typed answer repaired: a reply that doesn't parse, then one that doesn't validate (MaxRepairs 2);
//      then the same with MaxRepairs 1, which fails the node.
//   3. Refusals go back to the model as {"error": ...}; a tool's own failure fails the node, unless
//      ToolFailures.ReturnToModel says to tell the model instead.
//   4. MaxTurns: too many tool turns fail the node transiently, and a retry starts the conversation over.

using System.Text.Json;
using System.Text.Json.Nodes;
using Datalium.Strata.AI;
using Datalium.Strata.Demos.AgentToolLoop;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Datalium.Strata.Tools;
using Microsoft.Extensions.AI;

const string AgentId = "support-agent";
var store = new InMemoryCheckpointStore();

// --- Scenario 1: the model <-> tool loop. ---

Console.WriteLine("=== Scenario 1: look the order up, refund it, answer ===");
var desk = new SupportDesk();
var tools = AgentTools(desk);

// Turn 1 makes two calls at once; the model knows the tools only by their provider-safe names. Turn 2 refunds the
// total the lookup returned. Turn 3 has no tool calls, so it is the answer: JSON matching the RefundDecision schema.
var model = new ScriptedModel(
    _ => Reply.Call(
        ("call_1", "orders_lookup", """{"orderId":"A-1001"}"""),
        ("call_2", "shipping_track", """{"orderId":"A-1001"}""")),
    c => Reply.Call(("call_3", "refunds_issue", $$"""{"orderId":"A-1001","amount":{{Total(c, "call_1")}},"reason":"damaged in transit"}""")),
    c =>
    {
        var refundId = JsonNode.Parse(ScriptedModel.ToolResult(c, "call_3"))!["result"]!.GetValue<string>();
        return Reply.Say($$"""{"orderId":"A-1001","outcome":"refunded","refunded":{{Total(c, "call_1")}},"refundId":"{{refundId}}","reply":"Sorry about the crushed box. I have refunded 39.90 to your card ({{refundId}})."}""");
    });
var agent = RefundAgent(model, tools);
AgentResult<RefundDecision>? seen = null;
var (result, events) = await Run(
    RefundGraph(agent, r => seen = r),
    "ticket-1",
    new Ticket("T-1", "C-77", "My order A-1001 arrived with the box crushed and the lamp broken."),
    store);
var ticket = ExpectCompleted(result);

Console.WriteLine("  The tools as the model sees them (ToolDeclarations.For(subset)):");
foreach (var tool in agent.Declarations.Tools)
{
    Console.WriteLine($"    {agent.Declarations.StrataName(tool.Name),-15} -> {tool.Name}");
}

Console.WriteLine("  The conversation after the prompt:");
PrintConversation(seen!.Messages);
Console.WriteLine($"  Turns: {seen.Turns}. Tool calls (Strata names, the model's ids):");
foreach (var call in seen.ToolCalls)
{
    Console.WriteLine($"    {call.Name,-15} {call.CallId}  {call.Result}");
}

Console.WriteLine("  ToolCallStarted events: " + string.Join(", ", events.OfType<ToolCallStarted>().Select(e => $"{e.Tool} {e.CallId} caller={e.CallerId}")));
var (input, output) = (model.Requests.Sum(r => r.InputTokens), model.Requests.Sum(r => r.OutputTokens));
Console.WriteLine($"  Usage summed over the turns: {seen.Usage.InputTokens} in, {seen.Usage.OutputTokens} out (the model reported {input} and {output}).");
Console.WriteLine($"  Output: {seen.Output}");
Console.WriteLine($"  Refunds issued: {string.Join(", ", desk.Refunds.Select(r => $"{r.RefundId} {r.OrderId} {r.Amount}"))}");
Console.WriteLine($"  Notify sent: {ticket.Sent}");

Check(agent.Declarations.Tools.Select(t => t.Name).SequenceEqual(["orders_lookup", "shipping_track", "refunds_issue"]),
    "expected the subset's three tools under provider-safe names, in registry order");
Check(ToolDeclarations.For(tools).Tools.Select(t => t.Name).SequenceEqual(agent.Declarations.Tools.Select(t => t.Name)),
    "expected the agent's declarations to be ToolDeclarations.For(its registry)");
Check(agent.Declarations.ProviderNameOf("accounts.close") is null, "expected accounts.close, outside the subset, not to be declared");
Check(model.Requests.All(r => r.Options!.Tools!.Select(t => t.Name).SequenceEqual(["orders_lookup", "shipping_track", "refunds_issue"])),
    "expected every turn to send the same declarations");
Check(seen.Turns == 3 && model.Requests.Count == 3, "expected three turns");
Check(seen.ToolCalls.Select(c => $"{c.Name} {c.CallId} {c.Result}").SequenceEqual(
    ["orders.lookup call_1 Succeeded", "shipping.track call_2 Succeeded", "refunds.issue call_3 Succeeded"]),
    "expected every call recorded under its Strata name with the model's id");
var toolMessages = seen.Messages.Where(m => m.Role == ChatRole.Tool).ToList();
Check(toolMessages.Count == 2 && toolMessages[0].Contents.OfType<FunctionResultContent>().Select(r => r.CallId).SequenceEqual(["call_1", "call_2"]),
    "expected turn 1's two results in one tool message, in the model's order");
Check(events.OfType<ToolCallStarted>().Select(e => $"{e.Tool} {e.CallId} {e.CallerId}").SequenceEqual(
    ["orders.lookup call_1 support-agent", "shipping.track call_2 support-agent", "refunds.issue call_3 support-agent"]),
    "expected the registry's events to see the Strata names, the agent as caller and the model's ids");
Check(seen.Usage.InputTokens == input && seen.Usage.OutputTokens == output, "expected the usage summed over the turns");
Check(seen.Output == new RefundDecision("A-1001", "refunded", 39.90m, "RF-0001", seen.Output.Reply), "expected the parsed RefundDecision");
Check(desk.Refunds.Single() == ("RF-0001", "A-1001", 39.90m), "expected one refund of the order's total");
Check(ticket.Turns == 3 && ticket.Sent is not null, "expected the agent node's result folded into the state, and Notify to run after it");

// --- Scenario 2: a typed answer, parsed, validated and repaired. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: an answer that doesn't parse, then one that doesn't validate -- MaxRepairs 2 ===");
desk = new SupportDesk();
model = new ScriptedModel(DeclineScript(goodAnswerLast: true));
agent = RefundAgent(model, AgentTools(desk), maxRepairs: 2);
(result, _) = await Run(
    RefundGraph(agent, r => seen = r), "ticket-2", new Ticket("T-2", "C-12", "Refund A-1003 please, I just don't like it."), store);
ExpectCompleted(result);
var format = (ChatResponseFormatJson)model.Requests[0].Options!.ResponseFormat!;
var schema = JsonNode.Parse(format.Schema!.Value.GetRawText())!;
Console.WriteLine($"  Every turn asks for JSON schema '{format.SchemaName}': required [{string.Join(", ", schema["required"]!.AsArray())}], additionalProperties {schema["additionalProperties"]}.");
Console.WriteLine("  The conversation after the prompt:");
PrintConversation(seen!.Messages);
Console.WriteLine($"  Turns: {seen.Turns}, repair turns included. Output: {seen.Output}");
var repairs = seen.Messages.Skip(2).Where(m => m.Role == ChatRole.User).Select(m => m.Text).ToList();
Check(format.SchemaName == "RefundDecision" && schema["additionalProperties"]!.GetValue<bool>() == false,
    "expected the RefundDecision schema, closed to other properties");
Check(schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["orderId", "outcome", "refunded", "refundId", "reply"]),
    "expected every property required");
Check(model.Requests.All(r => r.Options!.ResponseFormat == format), "expected every turn to ask for the same format");
Check(repairs.Count == 2
    && repairs[0].StartsWith("Your answer was not valid: it is not JSON that matches the schema", StringComparison.Ordinal)
    && repairs[1] == "Your answer was not valid: outcome 'denied' is not one of refunded, declined, escalated. Answer again with one JSON object that matches the schema.",
    "expected a repair request for the parse failure, then one for the validation failure");
Check(seen.Turns == 4 && seen.ToolCalls.Count == 1 && seen.Output.Outcome == "declined", "expected the third answer to be the output");

Console.WriteLine();
Console.WriteLine("=== Scenario 2b: the same answers with MaxRepairs 1 -- the node fails ===");
model = new ScriptedModel(DeclineScript(goodAnswerLast: false));
agent = RefundAgent(model, AgentTools(new SupportDesk()));
(result, _) = await Run(
    RefundGraph(agent, r => seen = r), "ticket-2b", new Ticket("T-2", "C-12", "Refund A-1003 please, I just don't like it."), store);
var failed = ExpectNodeFailed(result);
Console.WriteLine($"  NodeFailed at '{failed.Node}' after {failed.Attempts} attempt(s): {failed.Message}");
Check(failed is { Node: "Resolve", Attempts: 1 }
    && failed.Message == "agent 'support-agent' gave no valid answer: outcome 'denied' is not one of refunded, declined, escalated",
    "expected the node to fail once the one repair was used");
Check(model.Requests.Count == 3, "expected no turn after the repair was used up");

// --- Scenario 3: refusals go back to the model; another tool failure fails the node. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: four bad calls in one turn -- each refused back to the model ===");
desk = new SupportDesk();
model = new ScriptedModel(
    _ => Reply.Call(
        ("call_1", "orders_cancel", """{"orderId":"A-1001"}"""),     // no such tool
        ("call_2", "accounts.close", """{"customerId":"C-77"}"""),   // a real tool, outside the agent's subset
        ("call_3", "orders_lookup", """{"orderId":1001}"""),         // a number where the tool takes a string
        ("call_4", "refunds_issue", """{"orderId":"A-1001","amou""")), // cut off: not a JSON object
    _ => Reply.Call(("call_5", "orders_lookup", """{"orderId":"A-1001"}""")),
    _ => Reply.Say("""{"orderId":"A-1001","outcome":"escalated","refunded":0,"refundId":null,"reply":"I can't close accounts. A colleague will contact you about A-1001 and your account."}"""));
agent = RefundAgent(model, AgentTools(desk));
(result, events) = await Run(
    RefundGraph(agent, r => seen = r), "ticket-3", new Ticket("T-3", "C-77", "Refund A-1001 and close my account."), store);
ExpectCompleted(result);
Console.WriteLine("  The conversation after the prompt:");
PrintConversation(seen!.Messages);
Console.WriteLine("  Agent's record:  " + string.Join(", ", seen.ToolCalls.Select(c => $"{c.Name} {c.Result}")));
Console.WriteLine("  Registry events: " + string.Join(", ", events.OfType<ToolCallCompleted>().Select(e => $"{e.Tool} {e.Result}")));
Console.WriteLine($"  Tools that actually ran: {string.Join(", ", desk.Calls)}");
var refused = seen.Messages.First(m => m.Role == ChatRole.Tool).Contents.OfType<FunctionResultContent>().Select(r => (string)r.Result!).ToList();
Check(refused.Count == 4
    && refused[0] == """{"error":"tool 'orders_cancel' was not found"}"""
    && refused[1] == """{"error":"tool 'accounts.close' is not permitted here"}"""
    && refused[2] == """{"error":"invalid arguments for tool 'orders_lookup': argument 'orderId' must be a string"}"""
    && refused[3] == """{"error":"the arguments for tool 'refunds_issue' aren't a JSON object"}""",
    "expected the four refusals in one tool message, in the agent's words");
Check(seen.ToolCalls.Select(c => c.Result).SequenceEqual(
    [ToolCallResult.Refused, ToolCallResult.Refused, ToolCallResult.Refused, ToolCallResult.Refused, ToolCallResult.Succeeded]),
    "expected four refusals and one success");
Check(events.OfType<ToolCallCompleted>().Select(e => $"{e.Tool} {e.Result}").SequenceEqual(
    ["orders_cancel Refused", "accounts.close Refused", "orders.lookup Failed", "orders.lookup Succeeded"]),
    "expected the registry to record the bad arguments as Failed, and never to see the call that wasn't a JSON object");
Check(desk.Calls.SequenceEqual(["orders.lookup(A-1001)"]), "expected only the good lookup to run: no account closed, no refund");

// An excluded tool is never declared, so its provider-safe name maps to nothing: "accounts_close" would be not
// found, not "not permitted".
Check(agent.Declarations.StrataName("accounts_close") == "accounts_close", "expected no mapping for an undeclared tool");

Console.WriteLine();
Console.WriteLine("=== Scenario 3b: the refund gateway fails -- ToolFailures.FailNode (the default), Retry(2) ===");
desk = new SupportDesk { GatewayFailure = "card ending 4242 is closed" };
model = new ScriptedModel(GatewayScript());
agent = RefundAgent(model, AgentTools(desk));
(result, events) = await Run(
    RefundGraph(agent, r => seen = r, RecoveryPolicy.Retry(2)), "ticket-3b",
    new Ticket("T-4", "C-77", "My order A-1001 arrived broken."), store);
failed = ExpectNodeFailed(result);
Console.WriteLine($"  NodeFailed at '{failed.Node}' after {failed.Attempts} attempt(s): {failed.Message}");
Check(failed is { Node: "Resolve", Attempts: 1 } && failed.Message == "tool 'refunds.issue' failed: card ending 4242 is closed",
    "expected the tool's failure, under its Strata name, to fail the node");
Check(!events.OfType<RetryScheduled>().Any(), "expected no retry: an unclassified exception is fatal");

Console.WriteLine();
Console.WriteLine("=== Scenario 3c: the same failure with ToolFailures.ReturnToModel ===");
desk = new SupportDesk { GatewayFailure = "card ending 4242 is closed" };
model = new ScriptedModel([.. GatewayScript(),
    _ => Reply.Say("""{"orderId":"A-1001","outcome":"escalated","refunded":0,"refundId":null,"reply":"Your card is closed, so a colleague will arrange the refund another way."}""")]);
agent = RefundAgent(model, AgentTools(desk), failures: ToolFailures.ReturnToModel);
(result, _) = await Run(
    RefundGraph(agent, r => seen = r), "ticket-3c", new Ticket("T-4", "C-77", "My order A-1001 arrived broken."), store);
ExpectCompleted(result);
Console.WriteLine("  The conversation after the prompt:");
PrintConversation(seen!.Messages);
Check(seen.ToolCalls.Select(c => $"{c.Name} {c.Result}").SequenceEqual(["orders.lookup Succeeded", "refunds.issue Failed"]),
    "expected the failure recorded as Failed");
Check((string)seen.Messages.Last(m => m.Role == ChatRole.Tool).Contents.OfType<FunctionResultContent>().Single().Result!
    == """{"error":"tool 'refunds_issue' failed: card ending 4242 is closed"}""",
    "expected the model to be told, under the name it called");
Check(seen.Output.Outcome == "escalated", "expected the model to escalate");

// --- Scenario 4: MaxTurns, and a retry that starts the conversation over. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: the model keeps polling -- MaxTurns 3, Retry(1) on the node ===");
desk = new SupportDesk();

// Four tool turns in a row: the fourth is one too many. Then the retry, a fresh conversation, where the script polls
// once and answers, as a real model might on a second try.
model = new ScriptedModel(
    _ => Reply.Call(("poll_1", "shipping_track", """{"orderId":"A-1002"}""")),
    _ => Reply.Call(("poll_2", "shipping_track", """{"orderId":"A-1002"}""")),
    _ => Reply.Call(("poll_3", "shipping_track", """{"orderId":"A-1002"}""")),
    _ => Reply.Call(("poll_4", "shipping_track", """{"orderId":"A-1002"}""")),
    _ => Reply.Call(("track_1", "shipping_track", """{"orderId":"A-1002"}""")),
    _ => Reply.Say("Your parcel A-1002 has left the Rotterdam hub and is on its way. There is no delivery date yet."));
var textAgent = new StrataAgent<string>(model, AgentTools(desk), new StrataAgentOptions<string>
{
    Id = AgentId,
    Chat = new ChatOptions { ModelId = "support-model-stand-in" },
    MaxTurns = 3,
});
AgentResult<string>? seenText = null;
var graph = new StateGraph<Ticket>()
    .AddAgentNode("Resolve", textAgent, Prompt, (t, r) => { seenText = r; return t with { Resolution = r.Output, Turns = r.Turns }; })
    .SetRecoveryPolicy("Resolve", RecoveryPolicy.Retry(1))
    .Compile();
(result, events) = await Run(graph, "ticket-4", new Ticket("T-5", "C-77", "Where is my parcel A-1002?"), store);
ticket = ExpectCompleted(result);
var retry = events.OfType<RetryScheduled>().Single();
var byAttempt = events.OfType<ToolCallStarted>().GroupBy(e => e.Attempt).Select(g => $"attempt {g.Key}: {g.Count()}");
Console.WriteLine($"  Attempt 1 failed transiently: {retry.Error}");
Console.WriteLine($"  Tool calls run, by attempt: {string.Join(", ", byAttempt)} (the 4th turn's call never ran).");
Console.WriteLine($"  Attempt 2's first request held {model.Requests[4].Messages.Count} messages: the prompt, nothing from attempt 1.");
Console.WriteLine($"  Turns in the result: {seenText!.Turns} (attempt 2 only). Answer: {ticket.Resolution}");
Check(retry.Error == "agent 'support-agent' made 3 tool turns without answering", "expected the MaxTurns failure, transient, to be retried");
Check(model.Requests.Count == 6, "expected four turns in attempt 1 and two in attempt 2");
Check(events.OfType<ToolCallStarted>().Count(e => e.Attempt == 1) == 3 && events.OfType<ToolCallStarted>().Count(e => e.Attempt == 2) == 1,
    "expected three calls in attempt 1 and one in attempt 2");
Check(model.Requests[4].Messages.Count == 2 && model.Requests[4].Messages.All(m => m.Role != ChatRole.Tool),
    "expected the retry to start the conversation over");
Check(seenText.Turns == 2 && seenText.ToolCalls.Single().CallId == "track_1", "expected the result to hold only the attempt that answered");
Check(model.Requests[0].Options!.ResponseFormat is null, "expected a text agent to ask for no response format");

Console.WriteLine();
Console.WriteLine("=== Done: tool calls dispatched and answered, a typed answer repaired, refusals told to the model, and a runaway loop retried from scratch. ===");

// The agent's tools: the desk's registry, narrowed to what a support agent may call. accounts.close stays out.
static ToolRegistry AgentTools(SupportDesk desk) =>
    new ToolRegistry().Register(desk).Subset("orders.lookup", "shipping.track", "refunds.issue");

static StrataAgent<RefundDecision> RefundAgent(
    IChatClient model, ToolRegistry tools, int maxRepairs = 1, ToolFailures failures = ToolFailures.FailNode) =>
    new(model, tools, new StrataAgentOptions<RefundDecision>
    {
        Id = AgentId,
        Chat = new ChatOptions { ModelId = "support-model-stand-in", Temperature = 0 },
        MaxRepairs = maxRepairs,
        ToolFailures = failures,
        Validate = Validate,
    });

// What the schema can't say: an outcome from a fixed list, and a refund id for a refund.
static IReadOnlyList<string> Validate(RefundDecision decision)
{
    var problems = new List<string>();
    if (decision.Outcome is not ("refunded" or "declined" or "escalated"))
    {
        problems.Add($"outcome '{decision.Outcome}' is not one of refunded, declined, escalated");
    }

    if (decision.Outcome == "refunded" && (decision.RefundId is null || decision.Refunded <= 0))
    {
        problems.Add("a refund needs a refund id and an amount");
    }

    return problems;
}

static IEnumerable<ChatMessage> Prompt(Ticket ticket, NodeContext context) =>
[
    new(ChatRole.System,
        "You are Northwind Gear's support agent. Look an order up before deciding anything. Refund an order that " +
        "arrived damaged within 30 days of delivery; decline other refunds; escalate what you can't do."),
    new(ChatRole.User, $"Ticket {ticket.Id} from customer {ticket.Customer}: {ticket.Message}"),
];

// Resolve (the agent node) -> Notify. The agent node is an ordinary fallible node: edges, a recovery policy.
static CompiledGraph<Ticket> RefundGraph(
    StrataAgent<RefundDecision> agent, Action<AgentResult<RefundDecision>> observe, RecoveryPolicy? policy = null)
{
    var graph = new StateGraph<Ticket>()
        .AddAgentNode("Resolve", agent, Prompt, (ticket, result) =>
        {
            observe(result);
            return ticket with { Resolution = $"{result.Output.Outcome}: {result.Output.Reply}", Turns = result.Turns };
        })
        .AddNode("Notify", ticket => ticket with { Sent = $"to {ticket.Customer}: {ticket.Resolution}" })
        .AddEdge("Resolve", "Notify");
    if (policy is not null)
    {
        graph.SetRecoveryPolicy("Resolve", policy);
    }

    return graph.Compile();
}

// A lookup of A-1003 (delivered 70 days ago), then answers: one missing "reply" (inside a Markdown fence, which the
// agent strips), one with an outcome the validator refuses, and, if asked for, a good one.
static Func<IReadOnlyList<ChatMessage>, Reply>[] DeclineScript(bool goodAnswerLast)
{
    Func<IReadOnlyList<ChatMessage>, Reply>[] script =
    [
        _ => Reply.Call(("call_1", "orders_lookup", """{"orderId":"A-1003"}""")),
        _ => Reply.Say("```json\n{\"orderId\":\"A-1003\",\"outcome\":\"declined\",\"refunded\":0,\"refundId\":null}\n```"),
        _ => Reply.Say("""{"orderId":"A-1003","outcome":"denied","refunded":0,"refundId":null,"reply":"A-1003 was delivered 70 days ago, past our 30-day window."}"""),
    ];
    return goodAnswerLast
        ? [.. script, _ => Reply.Say("""{"orderId":"A-1003","outcome":"declined","refunded":0,"refundId":null,"reply":"A-1003 was delivered 70 days ago, past our 30-day window, so I can't refund it."}""")]
        : script;
}

static Func<IReadOnlyList<ChatMessage>, Reply>[] GatewayScript() =>
[
    _ => Reply.Call(("call_1", "orders_lookup", """{"orderId":"A-1001"}""")),
    c => Reply.Call(("call_2", "refunds_issue", $$"""{"orderId":"A-1001","amount":{{Total(c, "call_1")}},"reason":"damaged in transit"}""")),
];

// The order total from a lookup's result, as the JSON number it was.
static string Total(IReadOnlyList<ChatMessage> conversation, string callId) =>
    JsonNode.Parse(ScriptedModel.ToolResult(conversation, callId))!["result"]!["Total"]!.ToJsonString();

static async Task<(GraphExecutionResult<Ticket> Result, List<RunEvent> Events)> Run(
    CompiledGraph<Ticket> graph, string threadId, Ticket ticket, ICheckpointStore store)
{
    var channel = new RunEventChannel();
    var result = await graph.RunAsync(threadId, ticket, store, new RunOptions { Observer = channel });
    var events = new List<RunEvent>();
    using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await foreach (var e in channel.Reader.ReadAllAsync(patience.Token))
    {
        events.Add(e);
    }

    return (result, events);
}

// Everything after the two prompt messages: each model turn, each tool message, each repair request.
static void PrintConversation(IReadOnlyList<ChatMessage> messages)
{
    foreach (var message in messages.Skip(2))
    {
        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            Console.WriteLine($"    tool message, {results.Count} result(s):");
            foreach (var r in results)
            {
                Console.WriteLine($"      {r.CallId}: {r.Result}");
            }

            continue;
        }

        if (message.Role == ChatRole.User)
        {
            Console.WriteLine($"    repair request: {message.Text}");
            continue;
        }

        foreach (var content in message.Contents)
        {
            var line = content switch
            {
                FunctionCallContent call => $"model calls {call.Name} {Arguments(call)}  ({call.CallId})",
                TextContent text => $"model answers: {text.Text.ReplaceLineEndings(" ")}",
                _ => null,
            };
            if (line is not null)
            {
                Console.WriteLine($"    {line}");
            }
        }
    }
}

static string Arguments(FunctionCallContent call) =>
    call.Exception is not null ? "<not a JSON object>" : JsonSerializer.Serialize(call.Arguments);

static Ticket ExpectCompleted(GraphExecutionResult<Ticket> result) =>
    (result as GraphExecutionResult<Ticket>.Completed)?.State ?? throw new InvalidOperationException($"expected Completed, got: {result}");

static GraphExecutionResult<Ticket>.NodeFailed ExpectNodeFailed(GraphExecutionResult<Ticket> result) =>
    result as GraphExecutionResult<Ticket>.NodeFailed ?? throw new InvalidOperationException($"expected NodeFailed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

/// <summary>The agent's typed answer. Its JSON schema is the response format every turn asks for.</summary>
sealed record RefundDecision(string OrderId, string Outcome, decimal Refunded, string? RefundId, string Reply);

sealed record Ticket(string Id, string Customer, string Message, string? Resolution = null, int Turns = 0, string? Sent = null);
