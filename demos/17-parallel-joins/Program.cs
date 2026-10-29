// Parallel group controls and join policies (design/26-Parallel-Group-Controls-And-Joins.md).
//
// Demo 10's group waits for every branch and merges them all. This demo is about the options that
// change when a group is done and what its reducer sees, on a shipping desk that asks three
// carriers (swiftship, parcelpro, budgetpost) to quote for one parcel:
//
//   1. FirstSuccess        -- a race: the first quote wins, the others are cancelled (LostRace).
//   2. A failure mid-race  -- doesn't end the race while another carrier could still win.
//   3. MaxConcurrency = 1  -- with FirstSuccess it is a failover chain: a carrier is only asked when
//                            the one before it failed, and the rest never start.
//   4. FirstMatching       -- the first quote that passes a test, not just the first to arrive;
//                            with no match, KeepSucceeded falls back to every answer, FailGroup fails.
//   5. Quorum(2)           -- two quotes, then pick the cheaper.
//   6. Transient or fatal  -- a group that can't meet its join fails transiently only if a retry
//                            could meet it.
//   7. All + KeepSucceeded -- an outcomes reducer over every branch, with BranchTimeout and
//                            MaxConcurrency, on a fake clock.
//
// Who won and why the others stopped comes from run events: JoinDecided names the deciding branches,
// and BranchCompleted.Reason says why a branch ended early (LostRace, SiblingFailed, TimedOut).
//
// Nothing here depends on timing. A carrier answers at once, never (it waits for its token), when
// the run's events show another carrier has settled, or (scenario 7) after a delay on a fake clock,
// so the order of every race is fixed and the output is the same on every run.

using System.Collections.Concurrent;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Datalium.Strata.Validation;
using Microsoft.Extensions.Time.Testing;

var parcel = Shipment.For("2.4 kg, Lisbon -> Berlin");

// --- Scenario 1: FirstSuccess. ---

Console.WriteLine("=== Scenario 1: FirstSuccess -- three carriers race, the first quote wins ===");
var desk = new Desk();
var result = await desk.RunAsync(
    QuoteGraph(
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", Desk.Never, Offers(new Quote("swiftship", 24.50m, 1))))
            .Branch("parcelpro", desk.Call("parcelpro", Desk.Now, Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call("budgetpost", Desk.Never, Offers(new Quote("budgetpost", 6.90m, 5)))),
        new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.FirstSuccess },
        desk),
    parcel);
var shipment = ExpectCompleted(result);
desk.PrintStory();
desk.PrintOutcomes();
Console.WriteLine($"  Chosen: {shipment.Chosen} ({shipment.Note})");
await desk.CancellationSeen("swiftship");
await desk.CancellationSeen("budgetpost");
Console.WriteLine("  swiftship and budgetpost both saw their cancellation token fire.");
Check(shipment.Chosen?.Carrier == "parcelpro", "expected parcelpro to win the race");
Check(desk.Outcomes is { JoinMet: true, Deciding: ["parcelpro"] }, "expected parcelpro to decide the join");
Check(
    desk.Outcomes!.Select(o => o.Status).SequenceEqual([BranchStatus.Cancelled, BranchStatus.Succeeded, BranchStatus.Cancelled]),
    "expected the losers to be Cancelled");
Check(
    desk.Story().SequenceEqual(
    [
        "completed Quote/parcelpro Succeeded",
        "decided   first_success, met, deciding [parcelpro]",
        "completed Quote/swiftship Cancelled LostRace",
        "completed Quote/budgetpost Cancelled LostRace",
    ]),
    "expected JoinDecided between the winner's BranchCompleted and the losers'");

// --- Scenario 2: a failure doesn't end a race that can still be won. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 2: swiftship errors first -- the race goes on ===");
desk = new Desk();
result = await desk.RunAsync(
    QuoteGraph(
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", Desk.Now, Refuses(NodeException.Transient("503 from the rating API"))))
            .Branch("parcelpro", desk.Call("parcelpro", desk.After("Quote/swiftship"), Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call("budgetpost", Desk.Never, Offers(new Quote("budgetpost", 6.90m, 5)))),
        new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.FirstSuccess },
        desk),
    parcel);
shipment = ExpectCompleted(result);
desk.PrintStory();
Console.WriteLine($"  Chosen: {shipment.Chosen}");
Check(
    desk.Story().SequenceEqual(
    [
        "completed Quote/swiftship Failed -- 503 from the rating API",
        "completed Quote/parcelpro Succeeded",
        "decided   first_success, met, deciding [parcelpro]",
        "completed Quote/budgetpost Cancelled LostRace",
    ]),
    "expected the race to survive swiftship's failure");

// --- Scenario 3: MaxConcurrency = 1 + FirstSuccess = a failover chain. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 3: MaxConcurrency = 1 + FirstSuccess -- ask in order of preference, stop at the first answer ===");
desk = new Desk();
result = await desk.RunAsync(
    QuoteGraph(
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", Desk.Now, Refuses(NodeException.Transient("503 from the rating API"))))
            .Branch("parcelpro", desk.Call("parcelpro", Desk.Now, Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call("budgetpost", Desk.Now, Offers(new Quote("budgetpost", 6.90m, 5)))),
        new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.FirstSuccess, MaxConcurrency = 1 },
        desk),
    parcel);
shipment = ExpectCompleted(result);
desk.PrintStory(withStarts: true);
desk.PrintOutcomes();
Console.WriteLine($"  Carriers asked: {string.Join(", ", desk.Started)} (peak in flight: {desk.Peak}). budgetpost was never called and has no events.");
Check(
    desk.Started.SequenceEqual(["swiftship", "parcelpro"]) && desk.Calls("budgetpost") == 0,
    "expected budgetpost never to start");
Check(desk.Peak == 1, "expected one carrier at a time");
Check(
    desk.Story(withStarts: true).SequenceEqual(
    [
        "started   Quote/swiftship",
        "completed Quote/swiftship Failed -- 503 from the rating API",
        "started   Quote/parcelpro",
        "completed Quote/parcelpro Succeeded",
        "decided   first_success, met, deciding [parcelpro]",
    ]),
    "expected a held-back branch to get no events once the race is won");
Check(desk.Outcomes!["budgetpost"]!.Status == BranchStatus.Cancelled, "expected the reducer to see budgetpost as Cancelled");

// --- Scenario 4: FirstMatching, with and without a match. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 4a: FirstMatching(\"delivered within 2 days\") -- the first quote isn't good enough ===");
var withinTwoDays = JoinPolicy<Shipment>.FirstMatching(s => s.Offer!.Days <= 2, "delivered within 2 days");
desk = new Desk();
result = await desk.RunAsync(
    QuoteGraph(
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", desk.After("Quote/budgetpost"), Offers(new Quote("swiftship", 24.50m, 1))))
            .Branch("parcelpro", desk.Call("parcelpro", Desk.Never, Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call("budgetpost", Desk.Now, Offers(new Quote("budgetpost", 6.90m, 5)))),
        new ParallelGroupOptions<Shipment> { Join = withinTwoDays },
        desk),
    parcel);
shipment = ExpectCompleted(result);
desk.PrintStory();
Console.WriteLine($"  Chosen: {shipment.Chosen}. budgetpost answered first and Succeeded, but only JoinDecided says it didn't win.");
Check(shipment.Chosen?.Carrier == "swiftship", "expected the first matching quote to win");
Check(
    desk.Story().SequenceEqual(
    [
        "completed Quote/budgetpost Succeeded",
        "completed Quote/swiftship Succeeded",
        "decided   first_matching, met, deciding [swiftship]",
        "completed Quote/parcelpro Cancelled LostRace",
    ]),
    "expected FirstMatching to skip a succeeded branch that didn't match");

Console.WriteLine();
Console.WriteLine("=== Scenario 4b: no quote matches -- Partial = KeepSucceeded reduces over every answer ===");
var slowCarriers = (Desk d) => new ParallelBranches<Shipment>()
    .Branch("swiftship", d.Call("swiftship", Desk.Now, Offers(new Quote("swiftship", 19.80m, 3))))
    .Branch("parcelpro", d.Call("parcelpro", Desk.Now, Offers(new Quote("parcelpro", 14.20m, 4))))
    .Branch("budgetpost", d.Call("budgetpost", Desk.Now, Offers(new Quote("budgetpost", 6.90m, 5))));
desk = new Desk();
result = await desk.RunAsync(
    QuoteGraph(slowCarriers(desk), new ParallelGroupOptions<Shipment> { Join = withinTwoDays, Partial = PartialResults.KeepSucceeded }, desk),
    parcel);
shipment = ExpectCompleted(result);
Console.WriteLine($"  {desk.Story().Last()}");
desk.PrintOutcomes();
Console.WriteLine($"  Chosen: {shipment.Chosen} ({shipment.Note})");
Check(desk.Outcomes is { JoinMet: false, Deciding: [] }, "expected an unmet join");
Check(desk.Outcomes!.All(o => o.Status == BranchStatus.Succeeded), "expected every answer to reach the reducer");
Check(shipment.Chosen?.Carrier == "swiftship", "expected the fallback to pick the fastest quote");
Check(desk.Story().Last() == "decided   first_matching, met: no, deciding []", "expected JoinDecided to report the miss");

Console.WriteLine();
Console.WriteLine("=== Scenario 4c: the same miss under the default Partial = FailGroup ===");
desk = new Desk();
result = await desk.RunAsync(QuoteGraph(slowCarriers(desk), new ParallelGroupOptions<Shipment> { Join = withinTwoDays }, desk), parcel);
var failed = ExpectNodeFailed(result);
Console.WriteLine($"  Failed: {failed.Message} -- {desk.ErrorKind()} (a retry asks again, and quotes change)");
Check(failed.Message == "no branch matched the join (3 succeeded)", "expected the no-match message");
Check(desk.ErrorKind() == "transient", "expected a no-match to be transient");

// --- Scenario 5: Quorum(2), plain reducer. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 5: Quorum(2) -- wait for two quotes, take the cheaper ===");
desk = new Desk();
IReadOnlyList<string> handed = [];
var quorumGraph = new StateGraph<Shipment>()
    .AddParallelGroup(
        "Quote",
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", Desk.Never, Offers(new Quote("swiftship", 24.50m, 1))))
            .Branch("parcelpro", desk.Call("parcelpro", desk.After("Quote/budgetpost"), Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call("budgetpost", Desk.Now, Offers(new Quote("budgetpost", 6.90m, 5)))),
        (entry, offers) =>
        {
            handed = [.. offers.Select(s => s.Offer!.Carrier)];
            return entry with { Chosen = offers.Select(s => s.Offer!).MinBy(q => q.Euros), Note = "cheaper of two" };
        },
        new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.Quorum(2) })
    .Compile();
shipment = ExpectCompleted(await desk.RunAsync(quorumGraph, parcel));
desk.PrintStory();
Console.WriteLine($"  The plain reducer was handed: [{string.Join(", ", handed)}] -- declaration order, not completion order.");
Console.WriteLine($"  Chosen: {shipment.Chosen}");
Check(handed.SequenceEqual(["parcelpro", "budgetpost"]), "expected the reducer to get the deciding states in declaration order");
Check(desk.Story().Contains("decided   quorum, met, deciding [budgetpost, parcelpro]"), "expected Deciding in completion order");
Check(shipment.Chosen?.Carrier == "budgetpost", "expected the cheaper quote");

// --- Scenario 6: an unmet join -- can a retry still meet it? ---

Console.WriteLine();
Console.WriteLine("=== Scenario 6a: Quorum(2), two carriers refuse for good -- Retry(2) can't help ===");
desk = new Desk();
result = await desk.RunAsync(
    QuoteGraph(
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", desk.After("Quote/parcelpro"), Refuses(NodeException.Fatal("parcels over 2 kg not accepted"))))
            .Branch("parcelpro", desk.Call("parcelpro", Desk.Now, Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call("budgetpost", desk.After("Quote/parcelpro"), Refuses(NodeException.Fatal("no service to postcode 10115")))),
        new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.Quorum(2) },
        desk,
        RecoveryPolicy.Retry(2)),
    parcel);
failed = ExpectNodeFailed(result);
Console.WriteLine($"  Failed after {failed.Attempts} attempt(s), {desk.ErrorKind()}:");
Console.WriteLine($"    {failed.Message}");
Console.WriteLine("  3 branches - 2 fatal failures = 1, fewer than the 2 needed, so no retry was made.");
Check(failed.Attempts == 1 && desk.ErrorKind() == "fatal", "expected a fatal failure with no retry");
Check(
    failed.Message == "quorum of 2 not reached: 1 of 3 branches succeeded; branch 'swiftship': parcels over 2 kg not accepted; branch 'budgetpost': no service to postcode 10115",
    "expected the quorum message to name every failure in declaration order");

Console.WriteLine();
Console.WriteLine("=== Scenario 6b: one refuses for good, one is rate limited once -- Retry(2) meets the quorum ===");
desk = new Desk();
var rateLimited = 0;
result = await desk.RunAsync(
    QuoteGraph(
        new ParallelBranches<Shipment>()
            .Branch("swiftship", desk.Call("swiftship", desk.After("Quote/parcelpro"), Refuses(NodeException.Fatal("parcels over 2 kg not accepted"))))
            .Branch("parcelpro", desk.Call("parcelpro", Desk.Now, Offers(new Quote("parcelpro", 14.20m, 3))))
            .Branch("budgetpost", desk.Call<Shipment>("budgetpost", desk.After("Quote/parcelpro"), s => Interlocked.Increment(ref rateLimited) == 1
                ? throw NodeException.Transient("rate limited")
                : s with { Offer = new Quote("budgetpost", 6.90m, 5) })),
        new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.Quorum(2) },
        desk,
        RecoveryPolicy.Retry(2)),
    parcel);
ExpectCompleted(result);
var retry = desk.Events.OfType<RetryScheduled>().Single();
Console.WriteLine("  Attempt 1 failed transiently (3 branches - 1 fatal = 2, enough for the quorum):");
Console.WriteLine($"    {retry.Error}");
Console.WriteLine($"  Attempt 2 met the quorum, deciding [{string.Join(", ", desk.Outcomes!.Deciding)}].");
Console.WriteLine(
    $"  Calls -- swiftship: {desk.Calls("swiftship")}, parcelpro: {desk.Calls("parcelpro")}, budgetpost: {desk.Calls("budgetpost")}. The retry re-ran parcelpro too: there is no per-branch retry.");
Check(retry.Attempt == 1, "expected one retry, after attempt 1");
Check(
    retry.Error == "quorum of 2 not reached: 1 of 3 branches succeeded; branch 'swiftship': parcels over 2 kg not accepted; branch 'budgetpost': rate limited",
    "expected the first attempt's quorum message");
Check(desk.Outcomes!.Deciding.SequenceEqual(["parcelpro", "budgetpost"]), "expected the retry to meet the quorum");
Check(desk.Calls("parcelpro") == 2 && desk.Calls("budgetpost") == 2, "expected the retry to re-run every branch");

// --- Scenario 7: All + KeepSucceeded with BranchTimeout and MaxConcurrency, on a fake clock. ---

Console.WriteLine();
Console.WriteLine("=== Scenario 7: tracking five parcels -- 2 at a time, 45 s each, keep what answered ===");
var clock = new FakeTimeProvider();
desk = new Desk();
Func<NodeContext, CancellationToken, Task> thirtySeconds = (_, ct) => Task.Delay(TimeSpan.FromSeconds(30), clock, ct);
var trackingGraph = new StateGraph<Board>()
    .AddParallelGroupWithOutcomes(
        "Track",
        new ParallelBranches<Board>()
            .Branch("swiftship", desk.Call<Board>("swiftship", Desk.Never, b => b with { Status = "never answers" }))
            .Branch("parcelpro", desk.Call<Board>("parcelpro", thirtySeconds, b => b with { Status = "out for delivery" }))
            .Branch("budgetpost", desk.Call<Board>("budgetpost", thirtySeconds, b => b with { Status = "in transit, Leipzig hub" }))
            .Branch("northline", desk.Call<Board>("northline", Desk.Now, _ => throw NodeException.Fatal("unknown tracking number NL-77Q")))
            .Branch("coastal", desk.Call<Board>("coastal", thirtySeconds, b => b with { Status = "delivered" })),
        (entry, results) =>
        {
            desk.TrackingOutcomes = results;
            return entry with
            {
                Lines = [.. results.Where(r => r.Status == BranchStatus.Succeeded).Select(r => $"{r.Name}: {r.State!.Status}")],
                Gaps = [.. results.Where(r => r.Status != BranchStatus.Succeeded).Select(r => $"{r.Name}: {r.Status} -- {r.Error?.Message}")],
            };
        },
        new ParallelGroupOptions<Board>
        {
            MaxConcurrency = 2,
            BranchTimeout = TimeSpan.FromSeconds(45),
            Partial = PartialResults.KeepSucceeded,
        })
    .Compile();
var board = ExpectCompleted(await desk.RunAsync(trackingGraph, new Board("order 4471", null, [], []), clock));
Console.WriteLine($"  Started, in declaration order: {string.Join(", ", desk.Started)}; peak in flight: {desk.Peak}.");
Console.WriteLine("  Tracked:");
foreach (var line in board.Lines)
{
    Console.WriteLine($"    - {line}");
}

Console.WriteLine("  Gaps:");
foreach (var gap in board.Gaps)
{
    Console.WriteLine($"    - {gap}");
}

var outcomes = desk.TrackingOutcomes!;
Console.WriteLine($"  JoinMet: {outcomes.JoinMet} (an All join with failures), and no JoinDecided event: an All group never emits one.");
Console.WriteLine(
    "  budgetpost waited 30 s for a slot, then ran 30 s: its 45 s timeout starts when it starts, so it wasn't charged for the wait.");
Check(desk.Started.SequenceEqual(["swiftship", "parcelpro", "budgetpost", "northline", "coastal"]), "expected starts in declaration order");
Check(desk.Peak == 2, "expected at most two branches in flight");
Check(
    outcomes.Select(o => o.Status).SequenceEqual(
        [BranchStatus.TimedOut, BranchStatus.Succeeded, BranchStatus.Succeeded, BranchStatus.Failed, BranchStatus.Succeeded]),
    "expected every outcome, in declaration order");
Check(
    outcomes["swiftship"]!.Error is { Kind: NodeFailureKind.Transient, Message: "branch 'swiftship' timed out after 45000 ms" },
    "expected a branch timeout to be a transient failure");
Check(outcomes["northline"]!.Error!.Kind == NodeFailureKind.Fatal, "expected northline's own error kind");
Check(!outcomes.JoinMet, "expected an unmet All join");
Check(!desk.Events.OfType<JoinDecided>().Any(), "expected no JoinDecided for an All group");
Check(
    desk.Events.OfType<BranchCompleted>().Single(e => e.NodePath == "Track/swiftship") is { Result: BranchResult.Failed, Reason: BranchEndReason.TimedOut },
    "expected swiftship's BranchCompleted to say TimedOut");

// --- Step 8: what .Compile() and the options refuse. ---

Console.WriteLine();
Console.WriteLine("=== Step 8: a quorum the group can't reach is refused at .Compile() ===");
try
{
    new StateGraph<Shipment>()
        .AddParallelGroup(
            "Quote",
            new ParallelBranches<Shipment>().Branch("swiftship", s => s).Branch("parcelpro", s => s).Branch("budgetpost", s => s),
            (entry, _) => entry,
            new ParallelGroupOptions<Shipment> { Join = JoinPolicy<Shipment>.Quorum(4) })
        .Compile();
    throw new InvalidOperationException("expected Quorum(4) of 3 branches to be refused");
}
catch (GraphCompilationException ex)
{
    var error = ex.Result.Errors.Single();
    Console.WriteLine($"  {error.Code} at '{error.Node}': {error.Message}");
    Check(error.Code == "STRATA_ERR_INVALID_QUORUM", "expected STRATA_ERR_INVALID_QUORUM");
}

try
{
    _ = new ParallelGroupOptions<Shipment> { MaxConcurrency = 0 };
    throw new InvalidOperationException("expected MaxConcurrency = 0 to be refused");
}
catch (ArgumentOutOfRangeException)
{
    Console.WriteLine("  MaxConcurrency = 0 is refused by the options themselves (ArgumentOutOfRangeException).");
}

Console.WriteLine();
Console.WriteLine("=== Done: every join decided as it should, and the events said who won and why the rest stopped. ===");

// The quote desk's graph: one group, "Quote", whose outcomes reducer takes the winner's quote when
// the join was met, and otherwise the fastest quote that came back.
static CompiledGraph<Shipment> QuoteGraph(
    ParallelBranches<Shipment> carriers, ParallelGroupOptions<Shipment> options, Desk desk, RecoveryPolicy? policy = null)
{
    var graph = new StateGraph<Shipment>()
        .AddParallelGroupWithOutcomes(
            "Quote",
            carriers,
            (entry, results) =>
            {
                desk.Outcomes = results;
                return results.JoinMet
                    ? entry with { Chosen = results[results.Deciding[0]]!.State!.Offer, Note = $"{results.Deciding[0]} decided the join" }
                    : entry with
                    {
                        Chosen = results.Succeeded.Select(s => s.Offer!).MinBy(q => q.Days),
                        Note = "no quote met the join: the fastest that answered",
                    };
            },
            options);
    if (policy is not null)
    {
        graph.SetRecoveryPolicy("Quote", policy);
    }

    return graph.Compile();
}

static Func<Shipment, Shipment> Offers(Quote quote) => shipment => shipment with { Offer = quote };

static Func<Shipment, Shipment> Refuses(NodeException error) => _ => throw error;

static T ExpectCompleted<T>(GraphExecutionResult<T> result) =>
    result is GraphExecutionResult<T>.Completed completed
        ? completed.State
        : throw new InvalidOperationException($"expected Completed, got: {result}");

static GraphExecutionResult<T>.NodeFailed ExpectNodeFailed<T>(GraphExecutionResult<T> result) =>
    result as GraphExecutionResult<T>.NodeFailed ?? throw new InvalidOperationException($"expected NodeFailed, got: {result}");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Quote(string Carrier, decimal Euros, int Days)
{
    public override string ToString() => $"{Carrier} EUR {Euros:0.00}, {Days} day(s)";
}

/// <summary>
/// The parcel, the quote one branch came back with (<see cref="Offer"/>), and the one the reducer
/// chose.
/// </summary>
sealed record Shipment(string Parcel, Quote? Offer, Quote? Chosen, string? Note)
{
    public static Shipment For(string parcel) => new(parcel, Offer: null, Chosen: null, Note: null);
}

/// <summary>Scenario 7's state: what each carrier said about its parcel, and who didn't say.</summary>
sealed record Board(string Order, string? Status, IReadOnlyList<string> Lines, IReadOnlyList<string> Gaps);

/// <summary>
/// One scenario's carriers and run: it builds the branches, counts calls and branches in flight, and
/// records the run's events, which a branch can wait on to answer after a sibling settled.
/// </summary>
sealed class Desk
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly Timeline _timeline = new();
    private readonly InMemoryCheckpointStore _store = new();
    private readonly ConcurrentDictionary<string, int> _calls = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cancelled = new();

    /// <summary>Answers at once.</summary>
    public static Func<NodeContext, CancellationToken, Task> Now { get; } = (_, _) => Task.CompletedTask;

    /// <summary>Never answers: waits until its token fires.</summary>
    public static Func<NodeContext, CancellationToken, Task> Never { get; } = (_, ct) => Task.Delay(Timeout.Infinite, ct);

    public BranchOutcomes<Shipment>? Outcomes { get; set; }

    public BranchOutcomes<Board>? TrackingOutcomes { get; set; }

    /// <summary>The branches the group started, in the order it started them (BranchStarted).</summary>
    public IReadOnlyList<string> Started =>
        [.. Events.OfType<BranchStarted>().Select(e => e.NodePath![(e.NodePath!.IndexOf('/') + 1)..])];

    /// <summary>
    /// The most branches the group had started and not yet settled, replayed from its events. This is
    /// what MaxConcurrency bounds. It is counted from the events, not inside the branches, because a
    /// group stops waiting for a branch the moment its token fires (a timeout, a lost race) and starts
    /// the next one while the abandoned branch's code may still be unwinding.
    /// </summary>
    public int Peak
    {
        get
        {
            var (open, peak) = (0, 0);
            foreach (var e in Events)
            {
                open += e switch { BranchStarted => 1, BranchCompleted => -1, _ => 0 };
                peak = Math.Max(peak, open);
            }

            return peak;
        }
    }

    public IReadOnlyList<RunEvent> Events => _timeline.Events;

    public int Calls(string carrier) => _calls.GetValueOrDefault(carrier);

    /// <summary>
    /// Answers once the run has reported the branch at <paramref name="path"/> settled in this attempt
    /// of the group (a retried group's branches report the new attempt).
    /// </summary>
    public Func<NodeContext, CancellationToken, Task> After(string path) =>
        (context, ct) => _timeline
            .WaitFor(e => e is BranchCompleted && e.NodePath == path && e.Attempt == context.Attempt)
            .WaitAsync(ct);

    /// <summary>A carrier's branch: waits as <paramref name="wait"/> says, then answers (or throws).</summary>
    public Func<T, NodeContext, CancellationToken, ValueTask<T>> Call<T>(
        string carrier, Func<NodeContext, CancellationToken, Task> wait, Func<T, T> answer) =>
        async (state, context, ct) =>
        {
            _calls.AddOrUpdate(carrier, 1, (_, n) => n + 1);
            try
            {
                await wait(context, ct);
                return answer(state);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled(carrier).TrySetResult();
                throw;
            }
        };

    /// <summary>
    /// Waits until <paramref name="carrier"/>'s branch saw its token fire. A group doesn't wait for the
    /// branches it gives up on, so this can land just after the run returns.
    /// </summary>
    public Task CancellationSeen(string carrier) => Cancelled(carrier).Task.WaitAsync(Patience);

    /// <summary>
    /// Runs <paramref name="graph"/>. With a fake <paramref name="clock"/>, advances it half a second at
    /// a time until the run ends, so timeouts and delays on the run's clock cost no real time.
    /// </summary>
    public async Task<GraphExecutionResult<T>> RunAsync<T>(CompiledGraph<T> graph, T start, FakeTimeProvider? clock = null)
    {
        var run = graph.RunAsync("desk", start, _store, new RunOptions { Observer = _timeline, TimeProvider = clock });
        for (var i = 0; clock is not null && !run.IsCompleted && i < 10_000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(1);
        }

        var result = await run.WaitAsync(Patience);

        // Events are delivered off the executor, so the last ones can arrive after RunAsync returns.
        await _timeline.WaitFor(e => e is RunCompleted).WaitAsync(Patience);
        return result;
    }

    /// <summary>The failed checkpoint's error kind, "transient" or "fatal".</summary>
    public string ErrorKind() => _store.Load("desk")!.Metadata!["error_kind"];

    /// <summary>The group's branch and join events, one line each, in order.</summary>
    public IReadOnlyList<string> Story(bool withStarts = false) =>
    [
        .. _timeline.Events.Select(e => e switch
        {
            BranchStarted when withStarts => $"started   {e.NodePath}",
            BranchCompleted c => $"completed {e.NodePath} {c.Result}"
                + (c.Reason is { } reason ? $" {reason}" : string.Empty)
                + (c.Error is { } error ? $" -- {error}" : string.Empty),
            JoinDecided j => $"decided   {j.Policy}, {(j.Met ? "met" : "met: no")}, deciding [{string.Join(", ", j.Deciding)}]",
            _ => null,
        }).OfType<string>(),
    ];

    public void PrintStory(bool withStarts = false)
    {
        Console.WriteLine("  Events:");
        foreach (var line in Story(withStarts))
        {
            Console.WriteLine($"    {line}");
        }
    }

    public void PrintOutcomes()
    {
        var outcomes = Outcomes!;
        Console.WriteLine(
            $"  Reducer saw (declaration order): {string.Join(", ", outcomes.Select(o => $"{o.Name} {o.Status}"))}; JoinMet: {outcomes.JoinMet}");
    }

    private TaskCompletionSource Cancelled(string carrier) =>
        _cancelled.GetOrAdd(carrier, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>
/// A run observer that keeps every event and lets a caller wait for one. The engine calls
/// <see cref="OnEvent"/> in sequence order, one event at a time, off the executor.
/// </summary>
sealed class Timeline : IRunObserver
{
    private readonly List<RunEvent> _events = [];
    private readonly List<(Func<RunEvent, bool> Match, TaskCompletionSource Seen)> _waiters = [];

    public IReadOnlyList<RunEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void OnEvent(RunEvent runEvent)
    {
        List<TaskCompletionSource> seen = [];
        lock (_events)
        {
            _events.Add(runEvent);
            foreach (var waiter in _waiters.Where(w => w.Match(runEvent)).ToList())
            {
                _waiters.Remove(waiter);
                seen.Add(waiter.Seen);
            }
        }

        foreach (var waiter in seen)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>Completes once an event matching <paramref name="match"/> has been seen (or at once, if one has).</summary>
    public Task WaitFor(Func<RunEvent, bool> match)
    {
        lock (_events)
        {
            if (_events.Any(match))
            {
                return Task.CompletedTask;
            }

            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((match, seen));
            return seen.Task;
        }
    }
}
