# 03-llm-tool-calling, explained

This is a guided walkthrough of the two source files in this demo,
[`Tools.cs`](Tools.cs) and [`Program.cs`](Program.cs), and of the Strata
features they use. For a shorter summary and setup steps, see
[`README.md`](README.md).

> ⚠️ **This demo calls a real AI model.** Each run makes two billed requests
> to OpenAI's Chat Completions API and needs `OPENAI_API_KEY` set. It and
> demo 06 are the only demos in this repo that go online. All the others run
> offline.

## The big picture in one paragraph

Demo [`02-tool-calling`](../02-tool-calling) built a tool named `"add"` and
called it with arguments the program supplied itself. This demo asks a **real
LLM** the question *"What is 47 plus 89?"* and lets the model **decide the
arguments**. The program doesn't talk to OpenAI itself. It builds an
**agent** (`StrataAgent<string>`, from the `Datalium.Strata.AI` package) out
of three things: a chat client for OpenAI, a **tool registry** holding the
`add` tool, and some options. The agent sends the question and the tool's
description to the model. The model replies "call `add` with
`{"a":47,"b":89}`". The agent runs that call through the registry (the same
generated dispatcher as demo 02), sends `{"result":136}` back, and the model
writes the final sentence. That loop is Strata's, not the demo's.

```
  Graph node "AskLlm"          StrataAgent (Datalium.Strata.AI)          OpenAI             ToolRegistry → generated code
  ───────────────────          ────────────────────────────────          ──────             ─────────────────────────────
  system + user message ────►  turn 1: messages + tool declarations ──►  reads the tools
                                                                         "call add {a:47,b:89}"
                               ◄──────────────────────────────────────── FunctionCallContent
                               DispatchToolAsync("add", ..., context) ─────────────────────► AddAsync(47, 89, context)
                               ◄──────────────────────────────────────────────────────────── {"result":136}
                               turn 2: history + tool result ─────────►  writes an answer
                               ◄──────────────────────────────────────── "47 plus 89 is 136."
  ◄──── AgentResult<string>
```

That's **two model calls** (two turns) with one local tool call between them.

---

## File 1: `Tools.cs`, the tool

```csharp
public partial class CalculatorTools
{
    [StrataTool(Name = "add", Description = "Adds two numbers and returns their sum.")]
    public static async Task<double> AddAsync(double a, double b, ToolCallContext context)
    {
        Console.WriteLine($"[add] {a} + {b}, called by agent '{context.CallerId}' (model call id {context.CallId}) in node '{context.NodePath}'");
        return await Task.FromResult(a + b);
    }
}
```

This is demo 02's tool. Briefly:

- **`[StrataTool]`** marks `AddAsync` as a tool named `"add"`.
- The class is **`partial`** because Strata's **source generator** writes the
  other half of it at build time. That half implements
  `IStrataToolProvider`: a JSON-Schema **manifest** of the tool (built from
  the method's parameters) and a **dispatcher** that turns `"add"` plus a
  JSON arguments string into a direct `AddAsync(a, b, context)` call,
  returning `{"result": ...}`.
- **`ToolCallContext context`** isn't one of the tool's arguments. The
  generator leaves it out of the manifest, so the model never sees it, and
  fills it from whoever dispatches the call. Here that's the agent, which
  sets **`CallerId`** to its own id (`calculator`) and **`CallId`** to the
  id the model gave the call. The log line shows both.

For how the generator works, including the generated code itself, see
[demo 02's explanation](../02-tool-calling/explanation.md).

**What's new in demo 03:** in demo 02 the program filled in `a` and `b`.
Here the model does, and Strata's agent hands them to the tool.

---

## File 2: `Program.cs`, step by step

### Step 1: settings and the API key

```csharp
const string DefaultModel = "gpt-4o-mini";

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine("OPENAI_API_KEY is not set. ...");
    return 1;
}

var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") is { Length: > 0 } named ? named : DefaultModel;
```

- The **API key** comes from an environment variable, never from the code.
  Secrets must not be committed to source control.
- **If the key is missing**, the program prints setup instructions to
  **stderr** and returns exit code `1`, before any network call. (Checked:
  running without the key prints that message and exits 1.)
- **The model** defaults to `gpt-4o-mini` (small and cheap) and can be changed
  with `OPENAI_MODEL`.

### Step 2: the chat client

```csharp
using var chat = CreateOpenAIClient(model, apiKey);

static IChatClient CreateOpenAIClient(string model, string apiKey) =>
    new OpenAI.Chat.ChatClient(
        model,
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(maxRetries: 0) })
    .AsIChatClient();
```

- **`IChatClient`** is `Microsoft.Extensions.AI`'s interface for a chat
  model. `Datalium.Strata.AI` takes any `IChatClient`, so OpenAI, Azure
  OpenAI, Ollama or a test fake all plug in the same way.
- **`OpenAI.Chat.ChatClient`** is the official OpenAI SDK's client, and
  **`.AsIChatClient()`** (from the `Microsoft.Extensions.AI.OpenAI` package)
  wraps it as an `IChatClient`. The name is written out in full because
  `OpenAI.Chat` and `Microsoft.Extensions.AI` both have a `ChatMessage`
  type.
- **`maxRetries: 0`** turns off the SDK's own retries. design/44 advises
  it: a `RecoveryPolicy` on the node should do the retrying, because Strata
  reads the provider's retry hint. This demo sets no policy; demo 06 does.

### Step 3: the registry and the agent

```csharp
var tools = new ToolRegistry().Register(new CalculatorTools());
var agent = new StrataAgent<string>(chat, tools, new StrataAgentOptions<string>
{
    Id = "calculator",
    MaxTurns = 2,
    ConfigureTurn = (turn, options) =>
    {
        if (turn.Number == 1)
        {
            options.ToolMode = ChatToolMode.RequireAny;
        }
    },
});
```

- **`ToolRegistry`** collects tool providers. `Register` adds every tool the
  generated `CalculatorTools` declares (just `add`). The registry is the
  whole set of tools the agent offers the model: nothing that isn't
  registered can be called.
- **`StrataAgent<string>`** is the model ↔ tool loop. The type argument is
  the answer's type. `string` makes it a **text agent**, whose answer is the
  model's text. (A record type would make it ask for JSON matching that
  type's schema; see [`23-agent-tool-loop`](../23-agent-tool-loop).)
- When it's built, the agent turns the registry's manifest into the
  model's **tool declarations** (`agent.Declarations`), under names a
  provider accepts. `add` is already a valid name. A Strata name such as
  `k8s.pods` would be sent as `k8s_pods` and mapped back when the model
  calls it.
- **`Id`** names the agent. It's the `CallerId` the tool sees, and it
  appears in the agent's failure messages and telemetry.
- **`MaxTurns = 2`** caps the turns that ask for tools. One addition needs
  one. A model that keeps asking for tools fails the node instead of running
  up the bill. (The default is 8.)
- **`ConfigureTurn`** requires a tool call on the first turn only; see
  [Forcing the tool call on the first turn only](#forcing-the-tool-call-on-the-first-turn-only).

### Step 4: the graph and its one node

```csharp
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
```

**`AddAgentNode`** adds a node that runs the agent. It takes two functions:

- **The prompt**: builds the messages from the state. Here a `system`
  message (instructions) and a `user` message (the question).
- **The apply step**: folds the agent's result into the next state.

What happens inside the node when it runs:

1. **Turn 1.** The agent calls the model with the two messages and the tool
   declarations. The model answers with a tool call:
   `add` with `{"a":47,"b":89}` and an id such as `call_abc123`.
   `Microsoft.Extensions.AI` gives it to the agent as a
   `FunctionCallContent`.
2. **The tool call.** The agent maps the name back to the Strata name and
   dispatches it through the registry, with a `ToolCallContext` built from
   the node's context plus `CallerId = "calculator"` and
   `CallId = "call_abc123"`. The generated dispatcher calls
   `AddAsync(47, 89, context)` and returns `{"result":136}`.
3. **Turn 2.** The agent adds the model's tool-call message and a tool
   message holding `{"result":136}` to the conversation and calls the model
   again. This time the model answers in words, with no tool calls, so that
   is the answer.

**The model never runs your code.** It only asks for a call. The agent
decides to run it, through the registry, which is where middleware,
permissions and tool telemetry live
([`26-tool-registry-and-middleware`](../26-tool-registry-and-middleware)).
If the model asked for a tool that isn't registered, or sent arguments that
don't fit, the agent would send `{"error": "…"}` back to the model instead
of failing.

**The result** is an `AgentResult<string>`:

| Property | Here |
|---|---|
| `Output` | The model's answer: *"47 plus 89 is 136."* |
| `Turns` | `2`: two model calls |
| `ToolCalls` | One `AgentToolCall`: `Name` `add`, `CallId` the model's id, `Result` `Succeeded` |
| `Messages` | The whole conversation: system, user, the model's tool call, the tool result, the answer |
| `Usage` | Input and output tokens summed over the turns (not printed here) |

The node is **fallible**, like every node `Datalium.Strata.AI` adds. If the
model call fails (a wrong key, an unknown model, a rate limit), the node
fails, and `RunAsync` returns a `Failed` result rather than throwing. A
`RecoveryPolicy` on the node could retry the transient ones; see demo 06.

### Step 5: reading the conversation back

```csharp
static string Describe(AgentToolCall call, IReadOnlyList<ChatMessage> messages)
{
    var contents = messages.SelectMany(m => m.Contents).ToList();
    var asked = contents.OfType<FunctionCallContent>().First(c => c.CallId == call.CallId);
    var answered = contents.OfType<FunctionResultContent>().First(r => r.CallId == call.CallId);
    return $"{call.Name} {JsonSerializer.Serialize(asked.Arguments)} -> {answered.Result} ({call.Result})";
}
```

`AgentToolCall` says which tool was called and how it ended. The arguments
the model chose and the result Strata sent back are in the conversation,
linked by the call id. `Describe` finds both and prints
`add {"a":47,"b":89} -> {"result":136} (Succeeded)`. `LastAddResult` reads
the number out of the last successful `add` result's JSON.

### Step 6: run, print and check

```csharp
ICheckpointStore store = new InMemoryCheckpointStore();
var outcome = await graph.RunAsync("llm-thread", new LlmState { Question = "What is 47 plus 89?" }, store);
...
Check(completed.ToolCalls.Count > 0, "the model answered without calling the add tool");
Check(completed.ToolResult == 136, ...);
Check(completed.Turns >= 2, ...);
```

- **`InMemoryCheckpointStore`**: `RunAsync` requires a checkpoint store, but
  this graph never pauses, so nothing durable is saved to it.
- **`GraphExecutionResult`**: `Completed` holds the final state. `Failed`
  (which includes a failed node) becomes an exception with the message.
- **The checks** cover what shouldn't vary between runs: the model called
  the tool, the tool returned 136, and there was a turn for the call and a
  turn for the answer. If one fails, the program throws and exits non-zero.
  The model's wording isn't checked.

### Step 7: the state type

```csharp
sealed record LlmState
{
    public required string Question { get; init; }
    public string? Answer { get; init; }
    public double? ToolResult { get; init; }
    public int Turns { get; init; }
    public IReadOnlyList<string> ToolCalls { get; init; } = [];
}
```

| Property | Set by | Notes |
|---|---|---|
| `Question` | The caller (`required`) | The input |
| `ToolResult` | The apply step | The **exact** number Strata's tool computed. You can trust it. |
| `Answer` | The apply step | The model's **wording**, which varies from run to run |
| `Turns`, `ToolCalls` | The apply step | What the agent did, for printing and checking |

Keeping the exact value apart from the model's wording is good practice.
Program logic should depend on `ToolResult`, not on parsing what the model
wrote.

---

## Forcing the tool call on the first turn only

The 0.3 version of this demo made its own HTTP calls and set OpenAI's
`tool_choice` to *require* `add` on the first call, then left it off on the
second. The agent does the same through `ConfigureTurn`:

```csharp
ConfigureTurn = (turn, options) =>
{
    if (turn.Number == 1)
    {
        options.ToolMode = ChatToolMode.RequireAny;
    }
},
```

The agent calls it before each model call, on that turn's own copy of the
options, after it has set the tools and the response format. `turn.Number`
starts at 1. Setting `ToolMode` on `StrataAgentOptions.Chat` instead would
require a tool on **every** turn: the model could never answer, and the run
would fail at `MaxTurns`. `ConfigureTurn` arrived late in the 0.9 work (it is
listed in the `0.9.0-preview.1` [`CHANGELOG.md`](https://github.com/datalium/strata/blob/main/CHANGELOG.md));
before it, this demo relied on the system prompt alone.

---

## The project file: `03-llm-tool-calling.csproj`

| Reference | Provides |
|---|---|
| `Datalium.Strata` | Graph engine, `[StrataTool]`, `ToolRegistry`, `ToolCallContext`, and the `[StrataTool]` source generator, which ships inside the package |
| `Datalium.Strata.AI` | `StrataAgent<T>`, `AddAgentNode`, `AgentResult<T>`, `ToolDeclarations` |
| `Datalium.Strata.Storage` | `InMemoryCheckpointStore` |
| `Microsoft.Extensions.AI.OpenAI` 10.10.1 | The OpenAI SDK and its `IChatClient` adapter. The version matches the `Microsoft.Extensions.AI.Abstractions` 10.10.1 that `Datalium.Strata.AI` uses. |

`Datalium.Strata.AI` itself depends only on `Microsoft.Extensions.AI`'s
abstractions, not on any provider. The OpenAI package is this demo's choice.

---

## Strata features used, at a glance

| Feature | Spec / design doc | Where it's used |
|---|---|---|
| `[StrataTool]` + source generator | STRATA-SPEC-003 | `Tools.cs`: defines the `add` tool |
| `ToolCallContext` on a tool | design/20 | `Tools.cs`: the agent's id and the model's call id |
| `ToolRegistry` | design/20 | The tools the agent may offer and call |
| `StrataAgent<string>` | design/44 | The model ↔ tool loop |
| `AddAgentNode` | design/44 | Runs the agent as the `"AskLlm"` node |
| `AgentResult<T>`, `AgentToolCall` | design/44 | What the agent did, read back and checked |
| `StateGraph<TState>`, `Compile()`, `RunAsync` | Core graph, STRATA-SPEC-004 | One node, validated, run |
| `GraphExecutionResult` | STRATA-SPEC-002 | `Completed` / `Failed` |

### The key idea: who does what

| Job | Done by |
|---|---|
| Describing the tool (name, description, parameter schema) | **Strata**, generated from the C# method |
| Talking to OpenAI | **The OpenAI SDK**, behind `IChatClient` |
| Deciding to call the tool, and with what arguments | **The LLM** |
| Running the loop: declare, dispatch, send back, repeat | **Strata**, `StrataAgent` |
| Validating the arguments and running the real code | **Strata**, through the registry and the generated dispatcher |
| Writing the final answer in words | **The LLM** |

## What this demo deliberately does *not* do

- **No streaming**: the model's reply arrives all at once. See
  [`06-token-streaming`](../06-token-streaming).
- **No typed answer**: this is a text agent. Typed answers, repairs,
  refused calls and `MaxTurns` firing are in
  [`23-agent-tool-loop`](../23-agent-tool-loop), with a scripted model.
- **No retries**: no `RecoveryPolicy`, so a provider error fails the run.
  See demo 06 and [`08-error-recovery`](../08-error-recovery).
- **No conversation beyond one run**: the agent's conversation lives for
  one attempt of its node.

## Try it yourself

```bash
# PowerShell:  $env:OPENAI_API_KEY = "sk-..."
export OPENAI_API_KEY=sk-...
dotnet run --project demos/03-llm-tool-calling/03-llm-tool-calling.csproj
```

Expected output, from a live run (the call id and the last line's wording
will vary):

```
[add] 47 + 89, called by agent 'calculator' (model call id call_NYQTcDbqjNtJvsh66D8MFTsd) in node 'AskLlm'
tool call: add {"a":47,"b":89} -> {"result":136} (Succeeded)
tool result: 136
model turns: 2
model: 47 plus 89 equals 136.
```

Some experiments (each run costs a little OpenAI usage):

1. **Run without the key**: you get the setup message and exit code 1, with
   no API call made.
2. **Change the question**, for example *"Add 1.5 and 2.25"*. The model
   extracts different arguments (and the `136` check fails, as it should).
3. **Ask something that needs two additions**, such as *"What is 1 + 2 + 3?"*.
   The model may make two calls in one turn, or one call in each of two
   turns; both fit in `MaxTurns = 2`, and `tool call:` prints each one.
   (The `136` check fails here too.) With `MaxTurns = 1`, the second kind
   fails the node.
4. **Print `agent.Declarations.Tools`** to see exactly what the model is told
   about the tool.
5. **Try another model** with `OPENAI_MODEL=gpt-4o`.
6. **Remove the system prompt**. The model may now answer "136" without the
   tool, and the first check says so.
