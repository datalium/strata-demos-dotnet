// Subgraph composition (design/15-Subgraph-Composition.md).
//
// A support desk. A supervisor node (Triage) reads a customer ticket and routes it -- with ordinary
// conditional edges -- to one of two specialist agents. Each agent is a *compiled graph of its own*,
// with its own state type, added to the desk graph as a single node with .AddSubgraph(...):
//
//   - BillingAgent -- BillingCase state; looks up the invoice, refunds a duplicate charge (a routing
//                     decision *inside* the child), drafts a reply.
//   - TechAgent    -- TechCase state; runs diagnostics, then a guarded apply-fix loop until the
//                     problem is gone. The diagnostics are themselves a subgraph (Probe state), so
//                     the tech agent nests one level deeper.
//
// The desk's Ticket knows nothing about BillingCase, TechCase or Probe. The only interface between a
// graph and its child is two mapper functions: one builds the child's starting state from the
// parent's, the other folds the child's finished state back in.
//
// What the scenarios show:
//
//   1. Composition -- a billing ticket, routed to a child graph with its own state and its own
//      conditional edge.
//   2. Nesting -- a tech ticket, whose agent loops internally and runs a subgraph of its own.
//   3. A child recovers itself -- a flaky API is retried by the child's own recovery policy; the desk
//      never sees the failure.
//   4. The parent's policy applies to the whole child -- when the child's retries run out on a
//      transient error, the desk's Retry re-runs the *entire* child, and its fallback routes to
//      Escalate on the state from *before* the agent ran.
//   5. Failures keep their kind, their path and their place -- a fatal error two levels down is not
//      retried at any level, and the error names every level. With no fallback, the desk persists a
//      Failed checkpoint of its own state that nests each child's own Failed checkpoint, so once the
//      cause is fixed a resume re-enters the innermost child at the node that failed.
//   6. A human in the loop, inside a child (design/16-Nested-Interrupts.md, design/21) -- a refund
//      needs approval, so BillingAgent has an interrupt boundary of its own. It freezes the *whole*
//      run: the desk's checkpoint nests the agent's, and a later ResumeAsync re-enters the agent
//      exactly where it stopped. The human's override goes to the agent's own state, and the recorded
//      decision belongs to the agent's level: the desk's nodes don't see it.
//   7. The desk's exported definition carries each child's whole graph (design/22).
//
// No live service and no LLM -- the "agents" are deterministic stand-ins, so the demo runs offline
// and prints the same thing every time.

using System.Text.Json;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Storage.Schema;

var store = new InMemoryCheckpointStore();

// --- Scenario 1: a child graph with its own state and routing. ---

Console.WriteLine("=== Scenario 1: a billing ticket -- BillingAgent is a subgraph ===");
var api = new BillingApi(failFirst: 0);
var desk = BuildSupportDesk(
    BuildBillingAgent(api, ownRetries: 0),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 0, Fallback: false));
var ticket = ExpectCompleted(await desk.RunAsync(
    "ticket-1", Ticket.New("I was charged twice for invoice 1042"), store));
PrintTicket(ticket);
Console.WriteLine(
    "  The desk's Ticket has no invoice or refund fields -- the agent's own BillingCase did the work, and the output mapper folded its steps back in.");
Check(
    ticket.Log.SequenceEqual(
    [
        "triage: billing",
        "billing: found invoice for $49.99 (charged twice)",
        "billing: refunded $49.99",
        "billing: drafted reply",
        "reply sent",
    ]),
    "expected the billing agent's steps folded into the desk's log");
Check(ticket.Reply == "We charged you twice and have refunded $49.99.", "expected the refund reply");

// --- Scenario 2: a child with a loop, and a grandchild. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: a tech ticket -- TechAgent loops, and nests a subgraph ===");
desk = BuildSupportDesk(
    BuildBillingAgent(new BillingApi(failFirst: 0), ownRetries: 0),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 0, Fallback: false));
ticket = ExpectCompleted(await desk.RunAsync("ticket-2", Ticket.New("My router keeps crashing"), store));
PrintTicket(ticket);
Console.WriteLine(
    "  Supervisor -> TechAgent -> Diagnose: three graphs deep, and the loop inside TechAgent is invisible to the desk.");
Check(
    ticket.Log.SequenceEqual(
    [
        "triage: tech",
        "tech: diagnostics: network ok",
        "tech: diagnostics: firmware 2.1 ok",
        "tech: diagnostics: 2 checks passed",
        "tech: fix attempt 1: no change",
        "tech: fix attempt 2: resolved",
        "tech: drafted reply",
        "reply sent",
    ]),
    "expected the nested diagnostics and the fix loop in the log");

// --- Scenario 3: the child recovers itself. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: the billing API times out twice -- the child's own Retry(2) ===");
api = new BillingApi(failFirst: 2);
desk = BuildSupportDesk(
    BuildBillingAgent(api, ownRetries: 2),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 0, Fallback: false));
ticket = ExpectCompleted(await desk.RunAsync("ticket-3", Ticket.New("Please refund my invoice"), store));
Console.WriteLine(
    $"  Completed after {api.Calls} billing API calls. The desk's policy (Retry 0) never fired: the child handled it.");
Console.WriteLine($"  Reply: {ticket.Reply}");
Check(api.Calls == 3, "expected the child to retry twice (3 calls)");
Check(store.Load("ticket-3") is null, "expected no checkpoint: nothing failed from the desk's point of view");
Check(!ticket.Log.Any(line => line.Contains("timed out")), "expected the timeouts to stay inside the child");

// --- Scenario 4: the parent's policy applies to the whole child. ---

Console.WriteLine();
Console.WriteLine(
    "=== Scenario 4: the billing API is down -- child Retry(1), desk Retry(1) + fallback 'Escalate' ===");
api = new BillingApi(failFirst: int.MaxValue);
desk = BuildSupportDesk(
    BuildBillingAgent(api, ownRetries: 1),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 1, Fallback: true));
ticket = ExpectCompleted(await desk.RunAsync("ticket-4", Ticket.New("Where is my refund?"), store));
Console.WriteLine(
    $"  {api.Calls} billing API calls: the child makes 2 per run (1 + its retry), and the desk's retry re-ran the whole child once.");
PrintTicket(ticket);
Console.WriteLine(
    "  Escalate ran on the ticket from before BillingAgent started, so none of the child's steps are in the log.");
Check(api.Calls == 4, "expected 2 child runs of 2 calls each");
Check(
    ticket.Log.SequenceEqual(["triage: billing", "escalated to a human", "reply sent"]),
    "expected the fallback to run on the pre-agent state");

// --- Scenario 5: a fatal error two levels down. ---

Console.WriteLine();
Console.WriteLine(
    "=== Scenario 5: the device firmware is too old -- fatal in Diagnose, desk Retry(3), no fallback ===");
desk = BuildSupportDesk(
    BuildBillingAgent(new BillingApi(failFirst: 0), ownRetries: 0),
    BuildTechAgent(Device.OldFirmware),
    new DeskPolicy(Retries: 3, Fallback: false));
var result = await desk.RunAsync("ticket-5", Ticket.New("My wifi is down"), store);
if (result is not GraphExecutionResult<Ticket>.NodeFailed failed)
{
    throw new InvalidOperationException($"expected NodeFailed, got: {result}");
}

Console.WriteLine($"  Failed: '{failed.Node}' gave up after {failed.Attempts} attempt(s):");
Console.WriteLine($"    {failed.Message}");
Console.WriteLine(
    "  The error names every level, and the fatal kind survived both hand-offs, so none of the 3 retries was used.");
Check(failed.Node == "TechAgent" && failed.Attempts == 1, "expected a single attempt at TechAgent");
Check(
    failed.Message ==
    "subgraph 'TechAgent' failed at 'Diagnose' after 1 attempt(s): " +
    "subgraph 'Diagnose' failed at 'CheckFirmware' after 1 attempt(s): " +
    "firmware 1.0 is too old to diagnose remotely",
    "expected the failure message to name every level");

var checkpoint = store.Load("ticket-5") ?? throw new InvalidOperationException("expected a Failed checkpoint");
var frozenLog = checkpoint.FrozenState!["Log"]!.ToJsonString();
Console.WriteLine(
    $"  Persisted checkpoint: {checkpoint.Status} at '{checkpoint.SuspendedNode}', error_kind = {checkpoint.Metadata!["error_kind"]}.");
Console.WriteLine($"  Its frozen state is the desk's Ticket from before TechAgent ran: log = {frozenLog}");
Check(checkpoint.Metadata["error_kind"] == "fatal", "expected the fatal kind to survive");
Check(frozenLog == "[\"triage: tech\"]", "expected the pre-agent ticket in the checkpoint");

// Each failed subgraph node nests its child's own Failed checkpoint, down to the node that failed.
Console.WriteLine($"  It nests each child's own Failed checkpoint (schema_version {checkpoint.SchemaVersion}):");
for (var level = checkpoint.Nested; level is not null; level = level.Nested)
{
    Console.WriteLine(
        $"    thread '{level.ThreadId}': {level.Status} at '{level.SuspendedNode}', state {level.FrozenState!.ToJsonString()}");
}

Console.WriteLine($"  Failed at: {string.Join("/", checkpoint.WaitingAt())}");
Check(
    checkpoint.WaitingAt().SequenceEqual(["TechAgent", "Diagnose", "CheckFirmware"]),
    "expected the failure to be recorded at the innermost node");

// Later -- the device's firmware has been updated. A new desk (and a new counter of network checks)
// resumes the Failed thread.
Console.WriteLine("  ... the device's firmware is updated, and the ticket is resumed ...");
var networkChecks = new Counter();
desk = BuildSupportDesk(
    BuildBillingAgent(new BillingApi(failFirst: 0), ownRetries: 0),
    BuildTechAgent(Device.Healthy, networkChecks),
    new DeskPolicy(Retries: 3, Fallback: false));
ticket = ExpectCompleted(await desk.ResumeAsync("ticket-5", HumanSignal.Approve("ops@support.example"), store));
PrintTicket(ticket);
Console.WriteLine(
    $"  {networkChecks.Count} network checks after the resume: it re-entered Diagnose at CheckFirmware, with the finding it had kept.");
Check(networkChecks.Count == 0, "expected the resume to skip the checks that had already passed");
Check(
    ticket.Log.SequenceEqual(
    [
        "triage: tech",
        "tech: diagnostics: network ok",
        "tech: diagnostics: firmware 2.1 ok",
        "tech: diagnostics: 2 checks passed",
        "tech: fix attempt 1: no change",
        "tech: fix attempt 2: resolved",
        "tech: drafted reply",
        "reply sent",
    ]),
    "expected the resumed run to finish the tech ticket");

// --- Scenario 6: a human's approval, inside a child. ---

Console.WriteLine();
Console.WriteLine(
    "=== Scenario 6: a refund needs a human's approval -- an interrupt boundary inside BillingAgent ===");
api = new BillingApi(failFirst: 0);
desk = BuildSupportDesk(
    BuildBillingAgent(api, ownRetries: 0, needsApproval: true),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 0, Fallback: false));
result = await desk.RunAsync("ticket-6", Ticket.New("I was charged twice for invoice 1042"), store);
if (result is not GraphExecutionResult<Ticket>.Interrupted interrupted)
{
    throw new InvalidOperationException($"expected Interrupted, got: {result}");
}

Console.WriteLine(
    $"  The whole run froze: suspended at '{interrupted.SuspendedNode}', and a resume will re-enter '{interrupted.NextNode}'.");
Console.WriteLine(
    $"  The desk's frozen Ticket is the one from before BillingAgent started: log = {JsonSerializer.Serialize(interrupted.FrozenState.Log)}");
Check(
    interrupted.SuspendedNode == "BillingAgent" && interrupted.NextNode == "BillingAgent",
    "expected the run to freeze at the subgraph node");
Check(interrupted.FrozenState.Log.SequenceEqual(["triage: billing"]), "expected the pre-agent ticket to be frozen");
Check(api.Calls == 1, "expected one billing API call before the freeze");

// What was persisted: the desk's checkpoint, with the agent's own inside it.
var frozen = store.Load("ticket-6") ?? throw new InvalidOperationException("expected the desk to persist a checkpoint");
var child = frozen.Nested ?? throw new InvalidOperationException("expected the agent's checkpoint nested in the desk's");
var childSteps = child.FrozenState!["Steps"]!.ToJsonString();
Console.WriteLine($"  Persisted: schema_version {frozen.SchemaVersion}, with the agent's own checkpoint nested inside:");
Console.WriteLine($"    thread '{child.ThreadId}' waiting at '{child.SuspendedNode}', its steps so far = {childSteps}");
Check(frozen.SchemaVersion == CheckpointSchema.NestedVersion, "expected a nested checkpoint to be schema 1.1.0");
Check(child.ThreadId == "BillingAgent" && child.SuspendedNode == "AwaitApproval", "expected the agent to wait at AwaitApproval");
Check(
    childSteps == "[\"found invoice for $49.99 (charged twice)\",\"refund of $49.99 awaits approval\"]",
    "expected the agent's steps so far in its own checkpoint");

// Later -- a different process, so a new desk and a new billing API whose call counter starts at zero:
// any call the agent made to it would show.
Console.WriteLine("  ... a reviewer approves, and adds a $5.00 goodwill credit ...");
var laterApi = new BillingApi(failFirst: 0);
var decisions = new DecisionLog();
desk = BuildSupportDesk(
    BuildBillingAgent(laterApi, ownRetries: 0, needsApproval: true, decisions),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 0, Fallback: false),
    decisions);
// A typed override: CreditCents is a number in BillingCase, the agent's state. The desk's Ticket has
// no such field. The innermost level that froze is the one the signal answers.
var approval = HumanSignal.Approve("dana@support.example").WithOverride("CreditCents", 500);
ticket = ExpectCompleted(await desk.ResumeAsync("ticket-6", approval, store));
PrintTicket(ticket);
Console.WriteLine(
    $"  {laterApi.Calls} billing API calls after the resume: the agent picked up at IssueRefund, right after its boundary, instead of starting over.");
Console.WriteLine(
    "  The override went to BillingAgent's own state ('CreditCents' is a BillingCase field) -- the desk's Ticket has no such field.");
Check(laterApi.Calls == 0, "expected the resumed agent not to call the billing API again");
Check(
    ticket.Log.SequenceEqual(
    [
        "triage: billing",
        "billing: found invoice for $49.99 (charged twice)",
        "billing: refund of $49.99 awaits approval",
        "billing: refunded $49.99 and credited $5.00 (approved by dana@support.example)",
        "billing: drafted reply",
        "reply sent",
    ]),
    "expected the agent to resume after its boundary with the approval applied");
Check(
    ticket.Reply == "We charged you twice and have refunded $49.99. We have also added a $5.00 credit.",
    "expected the credit in the reply");

// The decision is recorded at the level whose boundary was answered: the agent's.
foreach (var (path, decision) in decisions.Seen)
{
    var saw = decision is null ? "no decision" : $"{decision.Reviewer}'s approval at '{decision.Boundary}'";
    Console.WriteLine($"  {path} saw {saw}");
}

Check(
    decisions.Seen.Select(entry => (entry.Path, entry.Decision?.Reviewer)).SequenceEqual(
    [
        ("BillingAgent/IssueRefund", "dana@support.example"),
        ("SendReply", null),
    ]),
    "expected the decision inside the agent and none at the desk");
Console.WriteLine("  The decision belongs to BillingAgent's level. The desk's own nodes don't see it.");

// A frozen checkpoint is resumed once.
var again = await desk.ResumeAsync("ticket-6", new HumanSignal(IsApproved: true, ReviewerToken: "x"), store);
if (again is not GraphExecutionResult<Ticket>.Failed refused)
{
    throw new InvalidOperationException($"expected Failed, got: {again}");
}

Console.WriteLine($"  Resuming the same checkpoint again is refused: {refused.Message}");
Check(refused.Message == "checkpoint for thread 'ticket-6' was already resumed", "expected the second resume to be refused");

// --- Step 7: the parent's definition carries its children. ---

Console.WriteLine();
Console.WriteLine("=== Step 7: the desk's exported definition carries each child's whole graph ===");
var definition = BuildSupportDesk(
    BuildBillingAgent(new BillingApi(failFirst: 0), ownRetries: 0, needsApproval: true),
    BuildTechAgent(Device.Healthy),
    new DeskPolicy(Retries: 1, Fallback: true)).ToDefinition();
PrintDefinition("SupportDesk", definition, "  ");
var techAgent = definition.Nodes.Single(node => node.Name == "TechAgent");
Check(
    techAgent.Kind == GraphNodeKind.Subgraph
        && techAgent.Graph!.Nodes.Single(node => node.Name == "Diagnose").Graph!.Nodes.Select(node => node.Name)
            .SequenceEqual(["CheckNetwork", "CheckFirmware", "Summarize"]),
    "expected the definition to nest the grandchild's graph");
Console.WriteLine("  One definition describes all three levels, boundary included.");

Console.WriteLine();
Console.WriteLine(
    "=== Done: a supervisor routed to child graphs with their own state, failures kept their kind, their path and their place, the desk's policy governed each child as one node, and a human's approval froze the desk mid-agent and resumed inside it. ===");

// LookupInvoice -> (IssueRefund if it was a duplicate) -> DraftReply.
//
// With needsApproval (scenario 6) a human has to sign off first: LookupInvoice -> (AwaitApproval ->
// IssueRefund if it was a duplicate) -> DraftReply, where AwaitApproval is an interrupt boundary.
static CompiledGraph<BillingCase> BuildBillingAgent(
    BillingApi api, int ownRetries, bool needsApproval = false, DecisionLog? decisions = null)
{
    var graph = new StateGraph<BillingCase>()
        .AddFallibleNode("LookupInvoice", billing =>
        {
            var (chargeCents, duplicate) = api.LatestInvoice();
            return billing with
            {
                ChargeCents = chargeCents,
                Duplicate = duplicate,
                Steps = [.. billing.Steps, $"found invoice for {Dollars(chargeCents)}{(duplicate ? " (charged twice)" : "")}"],
            };
        })
        .SetRecoveryPolicy("LookupInvoice", RecoveryPolicy.Retry(ownRetries))
        .AddNode("IssueRefund", (billing, ctx, _) =>
        {
            // After an approval, the agent's level has a decision: who approved, at which boundary.
            decisions?.Record(ctx);
            var credit = billing.CreditCents > 0 ? $" and credited {Dollars(billing.CreditCents)}" : "";
            var approvedBy = ctx.Decision is { } decision ? $" (approved by {decision.Reviewer})" : "";
            return ValueTask.FromResult(billing with
            {
                Steps = [.. billing.Steps, $"refunded {Dollars(billing.ChargeCents)}{credit}{approvedBy}"],
            });
        })
        .AddNode("DraftReply", billing =>
        {
            var reply = "Your invoice is correct as issued.";
            if (billing.Duplicate)
            {
                reply = $"We charged you twice and have refunded {Dollars(billing.ChargeCents)}.";
                if (billing.CreditCents > 0)
                {
                    reply += $" We have also added a {Dollars(billing.CreditCents)} credit.";
                }
            }

            return billing with { Reply = reply, Steps = [.. billing.Steps, "drafted reply"] };
        });

    // The refund step is AwaitApproval when a human has to sign off first. It gets an ordinary edge to
    // IssueRefund; the interrupt boundary is what stops the run there, once the node has finished.
    var refundStep = "IssueRefund";
    if (needsApproval)
    {
        refundStep = "AwaitApproval";
        graph
            .AddNode("AwaitApproval", billing => billing with
            {
                Steps = [.. billing.Steps, $"refund of {Dollars(billing.ChargeCents)} awaits approval"],
            })
            .AddInterruptBoundary("AwaitApproval")
            .AddEdge("AwaitApproval", "IssueRefund");
    }

    // A routing decision inside the child: refund only a duplicate charge.
    return graph
        .AddConditionalEdge("LookupInvoice", refundStep, billing => billing.Duplicate)
        .AddEdge("LookupInvoice", "DraftReply")
        .AddEdge("IssueRefund", "DraftReply")
        .Compile();
}

// CheckNetwork -> CheckFirmware -> Summarize, over its own Probe state.
static CompiledGraph<Probe> BuildDiagnostics(Device device, Counter? networkChecks) =>
    new StateGraph<Probe>()
        .AddNode("CheckNetwork", probe =>
        {
            networkChecks?.Increment();
            return probe with { Findings = [.. probe.Findings, "network ok"] };
        })
        .AddFallibleNode("CheckFirmware", probe => device switch
        {
            Device.Healthy => probe with { Findings = [.. probe.Findings, "firmware 2.1 ok"] },
            _ => throw NodeException.Fatal("firmware 1.0 is too old to diagnose remotely"),
        })
        .AddNode("Summarize", probe => probe with
        {
            Findings = [.. probe.Findings, $"{probe.Findings.Count} checks passed"],
        })
        .AddEdge("CheckNetwork", "CheckFirmware")
        .AddEdge("CheckFirmware", "Summarize")
        .Compile();

// Diagnose (a subgraph) -> ApplyFix (loops until fixed, at most 3 times) -> DraftReply. The loop lives
// entirely inside the child: the desk never sees it.
static CompiledGraph<TechCase> BuildTechAgent(Device device, Counter? networkChecks = null) =>
    new StateGraph<TechCase>()
        .AddSubgraph(
            "Diagnose",
            BuildDiagnostics(device, networkChecks),
            _ => new Probe([]),
            (tech, probe) => tech with
            {
                Steps = [.. tech.Steps, .. probe.Findings.Select(finding => $"diagnostics: {finding}")],
            })
        .AddNode("ApplyFix", tech =>
        {
            var attempts = tech.Attempts + 1;
            // The simulated device only responds to the second attempt.
            var fixedNow = attempts >= 2;
            return tech with
            {
                Attempts = attempts,
                Fixed = fixedNow,
                Steps = [.. tech.Steps, $"fix attempt {attempts}: {(fixedNow ? "resolved" : "no change")}"],
            };
        })
        .AddNode("DraftReply", tech => tech with
        {
            Reply = $"Fixed after {tech.Attempts} attempt(s).",
            Steps = [.. tech.Steps, "drafted reply"],
        })
        .AddConditionalEdge("ApplyFix", "ApplyFix", tech => !tech.Fixed && tech.Attempts < 3)
        .AddEdge("ApplyFix", "DraftReply")
        .AddEdge("Diagnose", "ApplyFix")
        .Compile();

// Triage -> BillingAgent | TechAgent | GeneralReply -> SendReply.
//
// Both agents share one recovery policy. When it names a fallback, Escalate is added as that node --
// it gets no ordinary edge in, so the recovery policy is the only way there.
static CompiledGraph<Ticket> BuildSupportDesk(
    CompiledGraph<BillingCase> billing, CompiledGraph<TechCase> tech, DeskPolicy policy, DecisionLog? decisions = null)
{
    // Triage is the supervisor: it classifies the ticket. (Keyword matching stands in for an LLM call.)
    var graph = new StateGraph<Ticket>()
        .AddNode("Triage", ticket =>
        {
            var text = ticket.Message.ToLowerInvariant();
            var category = new[] { "charge", "invoice", "refund" }.Any(text.Contains) ? "billing"
                : new[] { "router", "wifi", "crash" }.Any(text.Contains) ? "tech"
                : "general";
            return ticket with { Category = category, Log = [.. ticket.Log, $"triage: {category}"] };
        })
        .AddSubgraph(
            "BillingAgent",
            billing,
            _ => BillingCase.New(),
            (ticket, result) => ticket with
            {
                Reply = result.Reply,
                Log = [.. ticket.Log, .. result.Steps.Select(step => $"billing: {step}")],
            })
        .AddSubgraph(
            "TechAgent",
            tech,
            _ => TechCase.New(),
            (ticket, result) => ticket with
            {
                Reply = result.Reply,
                Log = [.. ticket.Log, .. result.Steps.Select(step => $"tech: {step}")],
            })
        .AddNode("GeneralReply", ticket => ticket with { Reply = "Thanks for getting in touch." })
        .AddNode("SendReply", (ticket, ctx, _) =>
        {
            decisions?.Record(ctx);
            return ValueTask.FromResult(ticket with { Log = [.. ticket.Log, "reply sent"] });
        })
        .AddConditionalEdge("Triage", "BillingAgent", ticket => ticket.Category == "billing")
        .AddConditionalEdge("Triage", "TechAgent", ticket => ticket.Category == "tech")
        .AddEdge("Triage", "GeneralReply")
        .AddEdge("BillingAgent", "SendReply")
        .AddEdge("TechAgent", "SendReply")
        .AddEdge("GeneralReply", "SendReply");

    var recovery = RecoveryPolicy.Retry(policy.Retries);
    if (policy.Fallback)
    {
        graph
            .AddNode("Escalate", ticket => ticket with
            {
                Reply = "A human agent will contact you shortly.",
                Log = [.. ticket.Log, "escalated to a human"],
            })
            .AddEdge("Escalate", "SendReply");
        recovery = recovery.WithFallback("Escalate");
    }

    return graph
        .SetRecoveryPolicy("BillingAgent", recovery)
        .SetRecoveryPolicy("TechAgent", recovery)
        .Compile();
}

static void PrintTicket(Ticket ticket)
{
    Console.WriteLine($"  Reply: {ticket.Reply ?? "(none)"}");
    Console.WriteLine("  Log:");
    foreach (var line in ticket.Log)
    {
        Console.WriteLine($"    - {line}");
    }
}

// One line per level: its nodes, a subgraph's child indented under it.
static void PrintDefinition(string name, GraphDefinition definition, string indent)
{
    var nodes = definition.Nodes.Select(node => node.Interrupt is not null ? $"{node.Name} [boundary]" : node.Name);
    Console.WriteLine($"{indent}{name} (schema_version {definition.SchemaVersion}): {string.Join(", ", nodes)}");
    foreach (var node in definition.Nodes.Where(node => node.Kind == GraphNodeKind.Subgraph))
    {
        PrintDefinition(node.Name, node.Graph!, indent + "  ");
    }
}

static string Dollars(long cents) => $"${cents / 100}.{cents % 100:00}";

static Ticket ExpectCompleted(GraphExecutionResult<Ticket> result) =>
    (result as GraphExecutionResult<Ticket>.Completed)?.State
    ?? throw new InvalidOperationException($"expected Completed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

/// <summary>The parent graph's state.</summary>
sealed record Ticket(string Message, string? Category, string? Reply, IReadOnlyList<string> Log)
{
    public static Ticket New(string message) => new(message, Category: null, Reply: null, Log: []);
}

/// <summary>
/// BillingAgent's state. <c>CreditCents</c> is a goodwill credit; only a reviewer's override sets it
/// (scenario 6).
/// </summary>
sealed record BillingCase(
    long ChargeCents, bool Duplicate, string Reply, IReadOnlyList<string> Steps, long CreditCents = 0)
{
    public static BillingCase New() => new(ChargeCents: 0, Duplicate: false, Reply: "", Steps: []);
}

/// <summary>TechAgent's state.</summary>
sealed record TechCase(int Attempts, bool Fixed, string Reply, IReadOnlyList<string> Steps)
{
    public static TechCase New() => new(Attempts: 0, Fixed: false, Reply: "", Steps: []);
}

/// <summary>The diagnostics subgraph's state, nested inside TechAgent.</summary>
sealed record Probe(IReadOnlyList<string> Findings);

enum Device
{
    Healthy,

    /// <summary>Its firmware is too old to diagnose remotely -- retrying can't help.</summary>
    OldFirmware,
}

/// <summary>The billing system. Times out for its first <c>failFirst</c> calls.</summary>
sealed class BillingApi(int failFirst)
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The customer's latest invoice: (amount in cents, charged twice?).</summary>
    public (long ChargeCents, bool Duplicate) LatestInvoice()
    {
        var call = Interlocked.Increment(ref _calls);
        if (call <= failFirst)
        {
            Console.WriteLine($"    billing API call {call}: timed out");
            throw NodeException.Transient("billing API timed out");
        }

        Console.WriteLine($"    billing API call {call}: ok");
        return (4999, true);
    }
}

/// <summary>How the desk reacts when an agent fails.</summary>
sealed record DeskPolicy(int Retries, bool Fallback);

sealed class Counter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}

/// <summary>What each recording node saw in <c>NodeContext.Decision</c>, by node path, in the order they ran.</summary>
sealed class DecisionLog
{
    public List<(string Path, HumanDecision? Decision)> Seen { get; } = [];

    public void Record(NodeContext ctx) => Seen.Add((ctx.NodePath, ctx.Decision));
}
