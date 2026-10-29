// The smallest possible Strata agent: a single-node graph that reads `Name`
// from its state and writes a `Greeting`. No tools, no streaming, no
// interruption -- just StateGraph -> Compile() -> RunAsync().

using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;

var graph = new StateGraph<HelloState>()
    .AddNode("Greet", state => state with { Greeting = $"Hello, {state.Name}!" })
    .Compile();

// RunAsync always takes an ICheckpointStore. By default a run writes a
// checkpoint only where it stops early (an interrupt, a wait, a failure, a
// cancellation), and this one never does, so the store stays empty.
ICheckpointStore store = new InMemoryCheckpointStore();
var result = await graph.RunAsync("hello-thread", new HelloState { Name = "World" }, store);

switch (result)
{
    case GraphExecutionResult<HelloState>.Completed completed:
        Console.WriteLine(completed.State.Greeting);
        break;
    case GraphExecutionResult<HelloState>.Failed failed:
        throw new InvalidOperationException($"graph execution failed: {failed.Message}");
    default:
        throw new InvalidOperationException($"unexpected result: {result}");
}

sealed record HelloState
{
    public required string Name { get; init; }
    public string? Greeting { get; init; }
}
