# 03 — llm-tool-calling

A live LLM (OpenAI, through `Microsoft.Extensions.AI`'s `IChatClient`)
decides to call the `add` tool from [`02-tool-calling`](../02-tool-calling),
instead of the program handing the tool its arguments. Strata's own agent
loop, `StrataAgent<T>` from `Datalium.Strata.AI`
(`design/44-LLM-Integration.md`), does the work in between: it declares the
`ToolRegistry`'s tools to the model, dispatches the call the model asks for,
sends the result back, and returns the model's answer. The demo has no
OpenAI request or response code of its own.

This demo makes real, billed calls to the OpenAI API. It and
[`06-token-streaming`](../06-token-streaming) are the only demos that do (see
the "Note on LLMs" in the root [README](../../README.md)). Everything else
stays offline.

## What it demonstrates

- `new OpenAI.Chat.ChatClient(model, key, options).AsIChatClient()` from the
  official `Microsoft.Extensions.AI.OpenAI` package: any `IChatClient` works
  with `Datalium.Strata.AI`, and this is the OpenAI one. The SDK's own
  retries are off (`ClientRetryPolicy(maxRetries: 0)`), as design/44
  advises, so a `RecoveryPolicy` on the node would do any retrying
- `new ToolRegistry().Register(new CalculatorTools())`: the registry is the
  whole set of tools the agent offers the model. The source-generated
  manifest from demo 02 becomes the model's tool declarations
  (`ToolDeclarations`), with no hand-written JSON
- `StrataAgent<string>` (a text agent) run as a graph node with
  `.AddAgentNode(...)`. The model asks for `add {"a":47,"b":89}`, the agent
  dispatches it through the registry, sends `{"result":136}` back, and the
  model's next turn is the answer
- The context the agent dispatches with: the tool takes a
  `ToolCallContext`, and its `CallerId` is the agent's id (`calculator`) and
  its `CallId` the model's id for the call
- `AgentResult<string>`: the answer (`Output`), the number of model calls
  (`Turns`), every tool call with how it ended (`ToolCalls`), and the whole
  conversation (`Messages`), which the demo reads to print the arguments
  the model chose and the result Strata gave back
- `MaxTurns = 2` as a cost guard: a model that keeps asking for tools fails
  the node instead of looping
- The demo checks what doesn't vary (the tool was called, it returned 136,
  there were at least two turns) and exits non-zero if not. Only the
  model's wording varies

The first turn requires a tool call: `StrataAgentOptions.ConfigureTurn` sets
`ChatOptions.ToolMode = ChatToolMode.RequireAny` on turn 1 only, which OpenAI
receives as `tool_choice: "required"`. Setting `ToolMode` on the agent's options
instead would apply to every turn, and the model could never answer. The demo
still fails with `the model answered without calling the add tool` if no call
was made.

## What it does *not* cover

- Streaming the answer token by token:
  [`06-token-streaming`](../06-token-streaming)
- Typed (JSON-schema) answers, repairs, refused tool calls and the
  `MaxTurns` limit firing, all with a scripted model and no key:
  [`23-agent-tool-loop`](../23-agent-tool-loop)
- Failing over between models: [`24-model-routes`](../24-model-routes)
- Retries of a provider fault: there's no `RecoveryPolicy` here. Demo 06
  shows a provider fault failing the node, and
  [`08-error-recovery`](../08-error-recovery) covers recovery policies
- Middleware, subsets and permissions on the registry:
  [`26-tool-registry-and-middleware`](../26-tool-registry-and-middleware)
- A conversation that lasts longer than one run of the node: the agent's
  conversation lives for one attempt

## Setup

Requires an OpenAI API key with access to the Chat Completions API.

macOS/Linux (bash/zsh):

```bash
export OPENAI_API_KEY=sk-...
# optional, defaults to gpt-4o-mini
export OPENAI_MODEL=gpt-4o-mini
```

Windows (PowerShell):

```powershell
$env:OPENAI_API_KEY = "sk-..."
# optional, defaults to gpt-4o-mini
$env:OPENAI_MODEL = "gpt-4o-mini"
```

Either way, this only sets the variable for the current shell session. For
it to persist across sessions, add the `export`/`$env:` line to your shell
profile, or set it as a permanent user/system environment variable
(`setx OPENAI_API_KEY "sk-..."` on Windows, which takes effect in new
terminals only).

Without `OPENAI_API_KEY` set, the demo prints a short setup message to
stderr and exits with code 1, before making any network call.

## Run it

```bash
dotnet run --project demos/03-llm-tool-calling/03-llm-tool-calling.csproj
```

Expected output, from a live run against OpenAI (`gpt-4o-mini`, the default
model, 2026-10-24). The model's call id and its wording vary from run to run;
the arguments, the tool result and the turn count shouldn't:

```
[add] 47 + 89, called by agent 'calculator' (model call id call_NYQTcDbqjNtJvsh66D8MFTsd) in node 'AskLlm'
tool call: add {"a":47,"b":89} -> {"result":136} (Succeeded)
tool result: 136
model turns: 2
model: 47 plus 89 equals 136.
```

## Files

- `Tools.cs` — the `[StrataTool]`-annotated `add` method, as in demo 02,
  logging the agent and the model's call id from its `ToolCallContext`
- `Program.cs` — the OpenAI client, the registry, the agent, a one-node
  graph and the checks
- `explanation.md` — a guided walkthrough
