// Tool-calling (STRATA-SPEC-003): a node dispatches a [StrataTool]-generated tool by name
// through the source-generated IStrataToolProvider, instead of computing the result inline.
// The node is async, so it awaits the tool and hands it a ToolCallContext built from its own
// NodeContext (design/20-Tool-Dispatch-And-Registry.md).

using System.Text.Json;
using Datalium.Strata.Demos.ToolCalling;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Tools;

var toolProvider = new CalculatorTools();

var graph = new StateGraph<CalculatorState>()
    .AddNode("Add", async (state, ctx, ct) =>
    {
        var argsJson = JsonSerializer.Serialize(new { a = state.A, b = state.B });
        var resultJson = await toolProvider.DispatchToolAsync("add", argsJson, ToolCallContext.From(ctx), ct);
        var sum = JsonDocument.Parse(resultJson).RootElement.GetProperty("result").GetDouble();
        return state with { Sum = sum };
    })
    .Compile();

ICheckpointStore store = new InMemoryCheckpointStore();
var result = await graph.RunAsync("calc-thread", new CalculatorState { A = 3, B = 5 }, store);

switch (result)
{
    case GraphExecutionResult<CalculatorState>.Completed completed:
        Console.WriteLine($"{completed.State.A} + {completed.State.B} = {completed.State.Sum}");
        Check(completed.State.Sum == 8, "the tool's sum came back into the state");
        break;
    case GraphExecutionResult<CalculatorState>.Failed failed:
        throw new InvalidOperationException($"graph execution failed: {failed.Message}");
    default:
        throw new InvalidOperationException($"unexpected result: {result}");
}

// The manifest is what an LLM would be shown. The context parameter is plumbing, not an
// argument the model fills in, so it must not appear there.
var parameters = JsonDocument.Parse(toolProvider.GetToolsManifest()).RootElement[0].GetProperty("parameters");
var names = parameters.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
Console.WriteLine($"manifest parameters for 'add': {string.Join(", ", names)}");
Check(names.SequenceEqual(["a", "b"]), "the ToolCallContext parameter is left out of the manifest");

static void Check(bool condition, string claim)
{
    if (!condition)
    {
        Console.Error.WriteLine($"CHECK FAILED: {claim}");
        Environment.Exit(1);
    }
}

sealed record CalculatorState
{
    public required double A { get; init; }
    public required double B { get; init; }
    public double? Sum { get; init; }
}
