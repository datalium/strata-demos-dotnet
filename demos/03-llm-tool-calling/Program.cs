// A live LLM (OpenAI, through Microsoft.Extensions.AI's IChatClient) decides to call the `add`
// tool from 02-tool-calling. Strata's own agent loop (StrataAgent, design/44-LLM-Integration.md)
// does the rest: it declares the registry's tools to the model, dispatches the call the model
// asks for through the ToolRegistry, sends the result back, and returns the model's answer.
//
// Requires OPENAI_API_KEY in the environment. Costs a small amount of real OpenAI usage each run.
// Or run it free against a local model: set OPENAI_BASE_URL to any OpenAI-compatible server
// (Ollama, vLLM, LM Studio) and OPENAI_MODEL to a model it serves that supports tool calls.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Datalium.Strata.AI;
using Datalium.Strata.Demos.LlmToolCalling;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Datalium.Strata.Tools;
using Microsoft.Extensions.AI;
using OpenAI;

const string DefaultModel = "gpt-4o-mini";

// OpenAI by default. OPENAI_BASE_URL points the same client at any OpenAI-compatible server
// instead, e.g. Ollama at http://localhost:11434/v1, where the key is optional.
var baseUrl = Env("OPENAI_BASE_URL");
var apiKey = Env("OPENAI_API_KEY");
var model = Env("OPENAI_MODEL");
if (baseUrl is null && apiKey is null)
{
    Console.Error.WriteLine(
        "OPENAI_API_KEY is not set. Get a key at https://platform.openai.com/api-keys " +
        "and export it, e.g.:\n\n    export OPENAI_API_KEY=sk-...\n\n" +
        "Or use a local model, e.g. with Ollama:\n\n" +
        "    export OPENAI_BASE_URL=http://localhost:11434/v1 OPENAI_MODEL=qwen2.5:3b\n");
    return 1;
}

if (baseUrl is not null && model is null)
{
    Console.Error.WriteLine("OPENAI_BASE_URL is set, so set OPENAI_MODEL too: a model your server has, e.g. qwen2.5:3b.");
    return 1;
}

using var chat = CreateOpenAIClient(model ?? DefaultModel, apiKey, baseUrl);

// The registry is what the agent offers the model: every tool registered here, nothing else.
var tools = new ToolRegistry().Register(new CalculatorTools());
var agent = new StrataAgent<string>(chat, tools, new StrataAgentOptions<string>
{
    Id = "calculator",
    // One addition needs one tool turn. A model that keeps calling tools fails the node
    // instead of running up the bill (demo 23 shows the limit firing).
    MaxTurns = 2,
    // Require a tool call on the first turn only, so the model must use `add`, then may answer.
    // ChatOptions.ToolMode alone would apply to every turn and the model could never answer.
    ConfigureTurn = (turn, options) =>
    {
        if (turn.Number == 1)
        {
            options.ToolMode = ChatToolMode.RequireAny;
        }
    },
});

var graph = new StateGraph<LlmState>()
    .AddAgentNode<LlmState, string>(
        "AskLlm",
        agent,
        (state, _) =>
        [
            new(ChatRole.System, "You are a calculator assistant. Always use the `add` tool for arithmetic instead of computing it yourself."),
            new(ChatRole.User, state.Question),
        ],
        (state, result) => state with
        {
            Answer = result.Output,
            Turns = result.Turns,
            ToolCalls = [.. result.ToolCalls.Select(call => Describe(call, result.Messages))],
            ToolResult = LastAddResult(result),
        })
    .Compile();

ICheckpointStore store = new InMemoryCheckpointStore();
var outcome = await graph.RunAsync("llm-thread", new LlmState { Question = "What is 47 plus 89?" }, store);

var completed = outcome switch
{
    GraphExecutionResult<LlmState>.Completed done => done.State,
    GraphExecutionResult<LlmState>.Failed failed => throw new InvalidOperationException($"graph execution failed: {failed.Message}"),
    _ => throw new InvalidOperationException($"unexpected result: {outcome}"),
};

foreach (var call in completed.ToolCalls)
{
    Console.WriteLine($"tool call: {call}");
}

Console.WriteLine($"tool result: {completed.ToolResult}");
Console.WriteLine($"model turns: {completed.Turns}");
Console.WriteLine($"model: {completed.Answer}");

// The model's wording varies; these don't. If the model answered without the tool, say so
// rather than print a number nobody computed.
Check(completed.ToolCalls.Count > 0, "the model answered without calling the add tool");
Check(completed.ToolResult == 136, $"expected the add tool to return 136, got {completed.ToolResult?.ToString() ?? "nothing"}");
Check(completed.Turns >= 2, "expected one turn for the tool call and one for the answer");
return 0;

// The SDK's own retries are off, as design/44 advises: a RecoveryPolicy on the node does the
// retrying, and it sees the provider's retry hint.
// A server that needs no key still gets one: the OpenAI client won't send an empty credential.
static IChatClient CreateOpenAIClient(string model, string? apiKey, string? baseUrl) =>
    new OpenAI.Chat.ChatClient(
        model,
        new ApiKeyCredential(apiKey ?? "not-needed"),
        new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            Endpoint = baseUrl is null ? null : new Uri(baseUrl),
        })
    .AsIChatClient();

static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

// "add {"a":47,"b":89} -> {"result":136} (Succeeded)": the call as the model made it and as
// Strata answered it, read back from the agent's conversation by the model's call id.
static string Describe(AgentToolCall call, IReadOnlyList<ChatMessage> messages)
{
    var contents = messages.SelectMany(m => m.Contents).ToList();
    var asked = contents.OfType<FunctionCallContent>().First(c => c.CallId == call.CallId);
    var answered = contents.OfType<FunctionResultContent>().First(r => r.CallId == call.CallId);
    return $"{call.Name} {JsonSerializer.Serialize(asked.Arguments)} -> {answered.Result} ({call.Result})";
}

// The number the last successful `add` call returned: the generated dispatcher answers {"result": <sum>}.
static double? LastAddResult(AgentResult<string> result)
{
    var last = result.ToolCalls.LastOrDefault(c => c.Name == "add" && c.Result == ToolCallResult.Succeeded);
    if (last is null)
    {
        return null;
    }

    var json = result.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
        .First(r => r.CallId == last.CallId).Result as string;
    if (json is null)
    {
        return null;
    }

    using var parsed = JsonDocument.Parse(json);
    return parsed.RootElement.GetProperty("result").GetDouble();
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record LlmState
{
    public required string Question { get; init; }
    public string? Answer { get; init; }
    public double? ToolResult { get; init; }
    public int Turns { get; init; }
    public IReadOnlyList<string> ToolCalls { get; init; } = [];
}
