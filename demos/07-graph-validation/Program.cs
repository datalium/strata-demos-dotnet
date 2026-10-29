// Compile-time graph validation (STRATA-SPEC-004): every earlier demo's `.Compile()` call ran the
// validator and passed. This one shows it rejecting a graph, and what it takes to pass.
//
// A document review pipeline is built twice from the same four nodes. Only the edges differ.
//
// - Broken: Review routes back to Draft on a plain `.AddEdge(...)`, and the exit edge to Archive
//   has a typo ("Archve"). `.Compile()` throws a GraphCompilationException before any node runs,
//   with three errors: STRATA_ERR_DANGLING_EDGE (the edge names a node that doesn't exist),
//   STRATA_ERR_ORPHAN (so Archive is unreachable) and STRATA_ERR_UNGUARDED_CYCLE (nothing bounds
//   the Draft/Review loop).
// - Fixed: the retry edge becomes an `.AddConditionalEdge(...)` guarded by a revision count, and
//   the typo is fixed. Inside the loop, Draft reaches Review two ways (through LegalReview on the
//   first pass, directly after that), both on plain edges. That branch-and-rejoin is fine: the
//   validator rejects a cycle only if it is made of plain edges, and every cycle here goes through
//   the guarded Review -> Draft edge. The graph compiles and runs both paths.

using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Validation;

// --- Broken: an unguarded retry loop, and a typo in the exit edge.
var brokenGraph = Nodes()
    .AddConditionalEdge("Draft", "LegalReview", state => !state.LegalCleared)
    .AddEdge("Draft", "Review")
    .AddEdge("LegalReview", "Review")
    // Every review sends the document straight back to Draft. Nothing bounds how many times
    // this can fire, so the Draft/Review loop is an unguarded cycle.
    .AddEdge("Review", "Draft")
    .AddEdge("Review", "Archve");

GraphCompilationException rejected;
try
{
    brokenGraph.Compile();
    throw new InvalidOperationException("expected the broken graph to fail compilation");
}
catch (GraphCompilationException ex)
{
    rejected = ex;
}

Console.WriteLine("Compilation rejected, as STRATA-SPEC-004 requires:");
foreach (var error in rejected.Result.Errors)
{
    Console.WriteLine($"  [{error.Code}] {error.Message}");
}

var errors = rejected.Result.Errors;
Check(errors.Count == 3, "exactly three errors are reported");
Check(
    errors.Any(e => e.Kind == ValidationErrorKind.DanglingEdge && e.Node == "Review" && e.RelatedNode == "Archve"),
    "the Review -> Archve edge is flagged as dangling");
Check(
    errors.Any(e => e.Kind == ValidationErrorKind.OrphanNode && e.Node == "Archive"),
    "Archive is flagged as an orphan node");
Check(
    errors.Any(e => e.Kind == ValidationErrorKind.UnguardedCycle && e.Node == "Review" && e.RelatedNode == "Draft"),
    "the Draft/Review loop is flagged as an unguarded cycle");

// --- Fixed: same nodes, the retry edge guarded, the typo fixed.
var fixedGraph = Nodes()
    // Legal signs off once: the first draft goes through LegalReview, later ones skip it.
    // Both paths rejoin at Review on plain edges.
    .AddConditionalEdge("Draft", "LegalReview", state => !state.LegalCleared)
    .AddEdge("Draft", "Review")
    .AddEdge("LegalReview", "Review")
    // The conditional edge is the "if" (still under three revisions, send it back); the plain
    // edge added right after it is the "else" (approved, file it away). Same
    // declaration-order-wins rule as 04-multi-node-graph's shipping branch.
    .AddConditionalEdge("Review", "Draft", state => state.Revision < 3)
    .AddEdge("Review", "Archive")
    .Compile();

Console.WriteLine();
Console.WriteLine("Same nodes, fixed: guarded the retry edge and fixed the typo -- compiles clean.");

ICheckpointStore store = new InMemoryCheckpointStore();
var initial = new DocumentState { Title = "Q3 roadmap" };

var result = await fixedGraph.RunAsync("roadmap-review-thread", initial, store);

switch (result)
{
    case GraphExecutionResult<DocumentState>.Completed completed:
        var state = completed.State;
        Console.WriteLine($"path: {string.Join(" -> ", state.Trail)}");
        Console.WriteLine($"'{state.Title}' approved after {state.Revision} revision(s), archived: {state.Archived}");
        Check(state.Revision == 3 && state.Approved && state.Archived, "the guarded loop ran three times and exited");
        Check(
            state.Trail.SequenceEqual(
                ["Draft", "LegalReview", "Review", "Draft", "Review", "Draft", "Review", "Archive"]),
            "both branches into Review were taken: through LegalReview once, directly twice");
        break;
    case GraphExecutionResult<DocumentState>.Failed failed:
        throw new InvalidOperationException($"graph execution failed: {failed.Message}");
    default:
        throw new InvalidOperationException($"unexpected result: {result}");
}

// The four nodes both versions share. Each appends its name to the trail, so the run shows its path.
static StateGraph<DocumentState> Nodes() => new StateGraph<DocumentState>()
    .AddNode("Draft", state => state with { Revision = state.Revision + 1, Trail = [.. state.Trail, "Draft"] })
    .AddNode("LegalReview", state => state with { LegalCleared = true, Trail = [.. state.Trail, "LegalReview"] })
    // Approved once it's survived three drafts.
    .AddNode("Review", state => state with { Approved = state.Revision >= 3, Trail = [.. state.Trail, "Review"] })
    .AddNode("Archive", state => state with { Archived = true, Trail = [.. state.Trail, "Archive"] })
    .SetEntryPoint("Draft");

static void Check(bool condition, string claim)
{
    if (!condition)
    {
        Console.Error.WriteLine($"CHECK FAILED: {claim}");
        Environment.Exit(1);
    }
}

sealed record DocumentState
{
    public required string Title { get; init; }
    public int Revision { get; init; }
    public bool LegalCleared { get; init; }
    public bool Approved { get; init; }
    public bool Archived { get; init; }
    public IReadOnlyList<string> Trail { get; init; } = [];
}
