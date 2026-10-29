// The loop circuit breaker: STRATA-SPEC-004's MaxNodeHops (design/24-Loop-Circuit-Breaker.md).
//
// A meeting-scheduling agent: ProposeSlot -> CheckCalendars -> back to ProposeSlot while someone is
// busy, else SendInvite. The back edge is guarded, so .Compile() accepts the graph -- validation is
// structural and can't know whether the guard ever turns false. With an attendee who is on leave
// all week it never does: the agent re-plans forever. In a real agent every pass is an LLM call.
//
// Compile(new CompilationOptions { MaxNodeHops = 12 }) caps every thread at 12 node starts:
//
//   1. A meeting that can be booked converges in 7 hops and never notices the limit.
//   2. One that can't is refused before hop 13: HopLimitExceeded, a Failed checkpoint with
//      error_kind hop_limit, and the count in its metadata as strata.hops.
//   3. A retry isn't a hop: a flaky calendar API doubles the handler calls, not the hops.
//   4. The count survives interrupts: with an organizer reviewing every proposal, each freeze and
//      resume (by a freshly built graph, as another process would) carries strata.hops forward,
//      and the thread trips at exactly the hop a run without interrupts does.
//   5. RunOptions.MaxNodeHops replaces the limit for one run: an operator fixes the attendee list
//      and lets the tripped thread go on; a test runs the graph with a tighter limit.
//
// Everything is deterministic: the calendars are a dictionary and the agent proposes slots in order.

using System.Text.Json.Nodes;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;

var store = new InMemoryCheckpointStore();
var limit12 = new CompilationOptions { MaxNodeHops = 12 };
var canBook = Meeting.New("Q4 planning", "Alice", "Ravi");
var cannotBook = Meeting.New("Offsite", "Alice", "Bob", "Dana");
Console.WriteLine($"Calendars: Ravi is busy {string.Join(" and ", Scheduler.Busy["Ravi"])}; Dana is on leave all week.");
Console.WriteLine($"The agent proposes, in order: {string.Join(", ", Scheduler.Slots)}, then starts over.");

// --- Scenario 1: a meeting that can be booked converges under the limit. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 1: Alice + Ravi -- the loop converges, the limit is never reached ===");
var calls = new Calls();
var graph = Scheduler.Build(calls, limit12);
var booked = ExpectCompleted(await graph.RunAsync("mtg-1", canBook, store));
Console.WriteLine($"  Compiled with MaxNodeHops = {graph.MaxNodeHops}.");
Console.WriteLine($"  Tried: {string.Join(" | ", booked.Tried)}");
Console.WriteLine($"  Nodes started: {calls.Started} ({calls}). Invite: {booked.Invite}");
Check(graph.MaxNodeHops == 12, "expected the compiled limit on the graph");
Check(calls.Started == 7, "expected 7 hops: three proposals, three checks, one invite");

// --- Scenario 2: a meeting that can't be booked is stopped before hop 13. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: Alice + Bob + Dana -- the agent re-plans until the breaker trips ===");
var stuckCalls = new Calls();
var stuckGraph = Scheduler.Build(stuckCalls, limit12);
var result = await stuckGraph.RunAsync("mtg-2", cannotBook, store);
var tripped = ExpectTripped(result);
var checkpoint = store.Load("mtg-2")!;
Console.WriteLine($"  HopLimitExceeded: node '{tripped.Node}', hops {tripped.Hops}, limit {tripped.Limit}");
Console.WriteLine($"  Message: {tripped.Message}");
Console.WriteLine($"  It refines Failed, so existing `is Failed` handling catches it: {result is GraphExecutionResult<Meeting>.Failed}");
Console.WriteLine($"  Nodes started: {stuckCalls.Started} ({stuckCalls}) -- the 13th never ran.");
PrintCheckpoint(checkpoint);
Check((tripped.Node, tripped.Hops, tripped.Limit) == ("ProposeSlot", 12, 12), "expected the trip before the 7th proposal");
Check(tripped.Message == "hop limit of 12 reached: node 'ProposeSlot' would be hop 13", "expected the engine's message");
Check((stuckCalls["ProposeSlot"], stuckCalls["CheckCalendars"], stuckCalls["SendInvite"]) == (6, 6, 0), "expected 12 nodes to run");
Check(checkpoint is { Status: CheckpointStatus.Failed, SuspendedNode: "ProposeSlot", NextNode: "ProposeSlot" }, "expected a failed checkpoint at the refused node");
Check(checkpoint.Metadata!["error_kind"] == "hop_limit", "expected error_kind hop_limit");
Check(checkpoint.Metadata["strata.hops"] == "12", "expected strata.hops = 12");
Check(!checkpoint.Metadata.ContainsKey("attempts"), "expected no attempts: nothing was attempted");

// --- Scenario 3: retries are not hops. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: the calendar API fails once per check -- Retry(1) on CheckCalendars ===");
calls = new Calls();
booked = ExpectCompleted(await Scheduler.Build(calls, new CompilationOptions { MaxNodeHops = 7 }, flakyCalendar: true)
    .RunAsync("mtg-3", canBook, store));
Console.WriteLine($"  MaxNodeHops = 7: completed. Handler calls: {calls.HandlerCalls} ({calls}); nodes started: {calls.Started}.");
Check(calls["CheckCalendars"] == 6 && calls.Started == 7, "expected 6 calendar attempts inside 7 hops");
calls = new Calls();
tripped = ExpectTripped(await Scheduler.Build(calls, new CompilationOptions { MaxNodeHops = 6 }, flakyCalendar: true)
    .RunAsync("mtg-3b", canBook, store));
Console.WriteLine($"  MaxNodeHops = 6: {tripped.Message}");
Console.WriteLine("  With a limit of N the N-th node runs; only the node that would be N+1 is refused.");
Check((tripped.Node, tripped.Hops) == ("SendInvite", 6), "expected the trip at the invite, hop 7");

// --- Scenario 4: the count survives interrupts and resumes. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4: the organizer reviews every proposal -- an interrupt boundary inside the loop ===");
calls = new Calls();
result = await Scheduler.Build(calls, limit12, organizerReviews: true).RunAsync("mtg-4", cannotBook, store);
var hopsAtEachFreeze = new List<string>();
for (var round = 0; result is GraphExecutionResult<Meeting>.Interrupted frozen && round < 20; round++)
{
    var hops = store.Load("mtg-4")!.Metadata!["strata.hops"];
    hopsAtEachFreeze.Add(hops);
    Console.WriteLine(
        $"  {(round == 0 ? "run" : $"resume {round}"),-9} -> Interrupted after {frozen.SuspendedNode} (proposal {frozen.FrozenState.Proposals}: {frozen.FrozenState.Slot}), strata.hops = {hops}");

    // A graph built from scratch, as another process would: the count comes from the checkpoint.
    result = await Scheduler.Build(calls, limit12, organizerReviews: true)
        .ResumeAsync("mtg-4", HumanSignal.Approve("organizer"), store);
}

tripped = ExpectTripped(result);
Console.WriteLine($"  resume {hopsAtEachFreeze.Count,-2} -> {tripped.Message}");
Console.WriteLine($"  Nodes started across {hopsAtEachFreeze.Count + 1} runs: {calls.Started} ({calls}) -- the same trip as scenario 2.");
Check(hopsAtEachFreeze.SequenceEqual(["1", "3", "5", "7", "9", "11"]), "expected strata.hops to carry on across every freeze");
Check((tripped.Node, tripped.Hops, tripped.Limit) == ("ProposeSlot", 12, 12), "expected the same trip as an uninterrupted run");
Check((calls["ProposeSlot"], calls["CheckCalendars"]) == (6, 6), "expected the same 12 nodes as an uninterrupted run");

// --- Scenario 5: RunOptions.MaxNodeHops replaces the limit for one run. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 5: RunOptions.MaxNodeHops -- one run's limit, higher or lower ===");
var startedBefore = stuckCalls.Started;
tripped = ExpectTripped(await stuckGraph.ResumeAsync("mtg-2", HumanSignal.Approve("ops-oncall"), store));
Console.WriteLine($"  Resuming scenario 2's thread as it is: {tripped.Message} (nodes run: {stuckCalls.Started - startedBefore})");
Check(stuckCalls.Started == startedBefore, "expected the resume to trip again before running anything");

// The operator drops Dana from the invite and gives the thread room to finish.
startedBefore = stuckCalls.Started;
var fixedUp = HumanSignal.Approve("ops-oncall").WithOverride("Attendees", new JsonArray("Alice", "Bob"));
booked = ExpectCompleted(await stuckGraph.ResumeAsync("mtg-2", fixedUp, store, new RunOptions { MaxNodeHops = 20 }));
Console.WriteLine("  Resumed with Attendees = [Alice, Bob] and RunOptions { MaxNodeHops = 20 }:");
Console.WriteLine($"    nodes run: {stuckCalls.Started - startedBefore} (hops 13-15, counted on from the stored 12). Invite: {booked.Invite}");
Check(stuckCalls.Started - startedBefore == 3 && booked.Invite is not null, "expected propose, check, invite");

tripped = ExpectTripped(await Scheduler.Build(new Calls(), limit12)
    .RunAsync("mtg-5", canBook, store, new RunOptions { MaxNodeHops = 5 }));
Console.WriteLine($"  A test runs the bookable meeting with RunOptions {{ MaxNodeHops = 5 }}: {tripped.Message}");
Check((tripped.Node, tripped.Limit) == ("CheckCalendars", 5), "expected a lower per-run limit to replace the compiled one");

var uncapped = Scheduler.Build(new Calls(), new CompilationOptions());
tripped = ExpectTripped(await uncapped.RunAsync("mtg-6", cannotBook, store, new RunOptions { MaxNodeHops = 4 }));
Console.WriteLine($"  A graph compiled with no limit (MaxNodeHops = {uncapped.MaxNodeHops?.ToString() ?? "null"}) gets one for a run: {tripped.Message}");
Check(tripped is { Node: "ProposeSlot", Limit: 4 }, "expected the per-run limit to apply to an uncapped graph");

// --- A limit below 1 is refused at compile time. ---

Console.WriteLine();
Console.WriteLine("=== Step 6: a limit below 1 is refused by Compile() ===");
try
{
    Scheduler.Build(new Calls(), new CompilationOptions { MaxNodeHops = 0 });
    throw new InvalidOperationException("expected MaxNodeHops = 0 to be refused");
}
catch (ArgumentOutOfRangeException ex)
{
    Console.WriteLine($"  Refused: {ex.GetType().Name}: {ex.Message.Split(Environment.NewLine)[0]}");
}

Console.WriteLine();
Console.WriteLine("=== Done: the breaker stopped a loop that never converged, counted through retries and interrupts, and let one run go further. ===");

static void PrintCheckpoint(Checkpoint checkpoint)
{
    var metadata = string.Join(", ", checkpoint.Metadata!.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key} = {m.Value}"));
    Console.WriteLine($"  Checkpoint: {checkpoint.Status} at {checkpoint.NextNode}, {checkpoint.FrozenState!["Proposals"]} proposals in the frozen state");
    Console.WriteLine($"    metadata {{ {metadata} }}");
}

static Meeting ExpectCompleted(GraphExecutionResult<Meeting> result) =>
    result is GraphExecutionResult<Meeting>.Completed completed
        ? completed.State
        : throw new InvalidOperationException($"expected Completed, got: {result}");

static GraphExecutionResult<Meeting>.HopLimitExceeded ExpectTripped(GraphExecutionResult<Meeting> result) =>
    result as GraphExecutionResult<Meeting>.HopLimitExceeded
        ?? throw new InvalidOperationException($"expected HopLimitExceeded, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"check failed: {message}");
    }
}

sealed record Meeting(
    string Title,
    IReadOnlyList<string> Attendees,
    int Proposals,
    string? Slot,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Tried,
    string? Invite)
{
    public static Meeting New(string title, params string[] attendees) => new(title, attendees, 0, null, [], [], null);
}

static class Scheduler
{
    public static readonly string[] Slots = ["Mon 09:00", "Mon 14:00", "Tue 10:00", "Tue 15:00", "Wed 11:00"];

    public static readonly Dictionary<string, string[]> Busy = new()
    {
        ["Ravi"] = ["Mon 09:00", "Mon 14:00"],
        ["Dana"] = Slots,
    };

    public static CompiledGraph<Meeting> Build(
        Calls calls, CompilationOptions options, bool flakyCalendar = false, bool organizerReviews = false)
    {
        var graph = new StateGraph<Meeting>()
            .AddNode("ProposeSlot", meeting =>
            {
                calls.Ran("ProposeSlot");
                return meeting with { Proposals = meeting.Proposals + 1, Slot = Slots[meeting.Proposals % Slots.Length] };
            })
            .AddFallibleNode("CheckCalendars", (meeting, ctx, _) =>
            {
                calls.Ran("CheckCalendars", firstAttempt: ctx.Attempt == 1);
                if (flakyCalendar && ctx.Attempt == 1)
                {
                    return ValueTask.FromException<Meeting>(NodeException.Transient("calendar API returned 503"));
                }

                var busy = meeting.Attendees.Where(a => Busy.GetValueOrDefault(a, []).Contains(meeting.Slot)).ToList();
                var outcome = busy.Count == 0 ? $"{meeting.Slot} free" : $"{meeting.Slot} {string.Join(", ", busy)} busy";
                return ValueTask.FromResult(meeting with { Conflicts = busy, Tried = [.. meeting.Tried, outcome] });
            })
            .SetRecoveryPolicy("CheckCalendars", RecoveryPolicy.Retry(1))
            .AddNode("SendInvite", meeting =>
            {
                calls.Ran("SendInvite");
                return meeting with { Invite = $"'{meeting.Title}' {meeting.Slot} for {string.Join(", ", meeting.Attendees)}" };
            })
            .AddEdge("ProposeSlot", "CheckCalendars")
            .AddConditionalEdge("CheckCalendars", "ProposeSlot", meeting => meeting.Conflicts.Count > 0)
            .AddEdge("CheckCalendars", "SendInvite");

        return (organizerReviews ? graph.AddInterruptBoundary("ProposeSlot") : graph).Compile(options);
    }
}

/// <summary>Counts handler calls per node, and node starts (a retry is a call but not a start).</summary>
sealed class Calls
{
    private readonly Dictionary<string, int> _calls = [];

    public int Started { get; private set; }

    public int HandlerCalls => _calls.Values.Sum();

    public int this[string node] => _calls.GetValueOrDefault(node);

    public void Ran(string node, bool firstAttempt = true)
    {
        _calls[node] = this[node] + 1;
        if (firstAttempt)
        {
            Started++;
        }
    }

    public override string ToString() =>
        $"ProposeSlot {this["ProposeSlot"]}, CheckCalendars {this["CheckCalendars"]}, SendInvite {this["SendInvite"]}";
}
