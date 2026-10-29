// Token streaming (STRATA-SPEC-001) of a live model answer through Datalium.Strata.AI
// (design/44-LLM-Integration.md). AnswerQuestion is a model node (AddModelNode): one call through
// StrataChat, which streams because the run streams (RunStreaming) and sends each text update to
// the run's stream as a Token chunk. The node still gets the whole answer back as one ChatResponse.
// A plain node after it (SummarizeAnswer) gets NodeStart/NodeEnd chunks like any node.
//
// A second run asks OpenAI for a model that doesn't exist. The provider's 404 becomes a fatal node
// failure, and the node's RecoveryPolicy, which would retry a transient one, doesn't retry it.
//
// Requires OPENAI_API_KEY in the environment. Costs a small amount of real OpenAI usage each run
// (the second run is refused before any tokens are generated).

using System.ClientModel;
using System.ClientModel.Primitives;
using Datalium.Strata.AI;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage;
using Datalium.Strata.Streaming;
using Microsoft.Extensions.AI;
using OpenAI;

const string DefaultModel = "gpt-4o-mini";
const string MissingModel = "strata-demo-no-such-model";
const string Question = "What makes Rust's borrow checker unusual compared to garbage collection?";

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine(
        "OPENAI_API_KEY is not set. Get a key at https://platform.openai.com/api-keys " +
        "and export it, e.g.:\n\n    export OPENAI_API_KEY=sk-...\n");
    return 1;
}

var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") is { Length: > 0 } named ? named : DefaultModel;

using var chat = CreateOpenAIClient(model, apiKey);
using var missing = CreateOpenAIClient(MissingModel, apiKey);

// --- 1. A live answer, streamed ---------------------------------------------------------------

Console.WriteLine("=== Streaming a live answer, one token at a time ===\n");
var (reader, completion) = BuildGraph(chat).RunStreaming("stream-thread", new LlmStreamState { Question = Question }, new InMemoryCheckpointStore());

var tokens = new List<string>();
var midLine = false; // the tokens written so far don't end in a newline
await foreach (var chunk in reader.ReadAllAsync())
{
    if (chunk.Type != StreamChunkType.Token && midLine)
    {
        Console.WriteLine();
        midLine = false;
    }

    switch (chunk.Type)
    {
        case StreamChunkType.NodeStart:
            Console.WriteLine($"[node start] {chunk.NodeName}");
            break;
        case StreamChunkType.NodeEnd:
            Console.WriteLine($"[node end]   {chunk.NodeName}");
            break;
        case StreamChunkType.Token:
            tokens.Add(chunk.Payload.ToString());
            Console.Write(chunk.Payload.ToString());
            midLine = true;
            break;
        case StreamChunkType.Retry:
            // A transient fault (a 429, a dropped stream) retries the node, which streams its
            // answer again from the start. The consumer drops what the failed attempt sent.
            Console.WriteLine($"[retry]      {chunk.NodeName}");
            tokens.Clear();
            break;
        case StreamChunkType.Error:
            Console.WriteLine($"[error]      {chunk.NodeName}: {chunk.Payload}");
            break;
    }
}

// Draining the reader shows chunks arrived during the run. Awaiting `completion` is what shows
// the run finished, and hands back the same final state RunAsync would have returned.
var answered = await completion switch
{
    GraphExecutionResult<LlmStreamState>.Completed done => done.State,
    var other => throw new InvalidOperationException($"expected Completed, got: {other}"),
};

Console.WriteLine();
Console.WriteLine("=== Done ===");
Console.WriteLine($"token_chunks: {tokens.Count}");
Console.WriteLine($"answer_char_count: {answered.AnswerCharCount}");
Check(tokens.Count > 1, "expected the answer to arrive as more than one Token chunk");
Check(string.Concat(tokens) == answered.Answer, "expected the node's whole answer to be exactly the tokens the consumer saw, in order");
Check(answered.AnswerCharCount == answered.Answer.Length, "expected SummarizeAnswer to run after AnswerQuestion, on its answer");

// --- 2. A provider fault ----------------------------------------------------------------------

Console.WriteLine($"\n=== A provider fault: asking for model '{MissingModel}' ===\n");
var (faultReader, faultCompletion) = BuildGraph(missing).RunStreaming("fault-thread", new LlmStreamState { Question = Question }, new InMemoryCheckpointStore());

var faultChunks = new List<StreamChunkType>();
await foreach (var chunk in faultReader.ReadAllAsync())
{
    faultChunks.Add(chunk.Type);
}

var failed = await faultCompletion as GraphExecutionResult<LlmStreamState>.NodeFailed
    ?? throw new InvalidOperationException("expected the run to end NodeFailed");

Console.WriteLine($"node failed: {failed.Node}, after {failed.Attempts} attempt(s)");
Console.WriteLine($"message: {failed.Message.Split('\n')[0].Trim()}");
Console.WriteLine($"chunks the consumer saw: {string.Join(", ", faultChunks)}");

// ModelFaults reads the SDK's ClientResultException: a 4xx other than 408/409/429 is the
// request's fault, so it fails the node fatally and the policy's two retries are never used.
Check(failed.Node == "AnswerQuestion", "expected the model node to be the one that failed");
Check(failed.Message.StartsWith("model call failed (4", StringComparison.Ordinal), "expected a 4xx from the provider, as the node's failure message");
Check(failed.Attempts == 1, "expected a fatal fault not to be retried");
Check(!faultChunks.Contains(StreamChunkType.Retry) && !faultChunks.Contains(StreamChunkType.Token), "expected no retry and no tokens");
return 0;

// The model node, a recovery policy for it, and a plain node after it. The policy retries a
// transient fault (429, 5xx, a broken stream) twice, after the provider's retry hint or else a
// jittered backoff from 1s; a fatal one isn't retried at all.
static CompiledGraph<LlmStreamState> BuildGraph(IChatClient client) =>
    new StateGraph<LlmStreamState>()
        .AddModelNode<LlmStreamState>(
            "AnswerQuestion",
            client,
            (state, _) =>
            [
                new(ChatRole.System, "You are a helpful assistant. Answer in two or three sentences."),
                new(ChatRole.User, state.Question),
            ],
            (state, response) => state with { Answer = response.Text },
            new ModelNodeOptions { Chat = new ChatOptions { MaxOutputTokens = 200 } })
        .SetRecoveryPolicy("AnswerQuestion", RecoveryPolicy.Retry(2).WithBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)))
        .AddNode("SummarizeAnswer", state => state with { AnswerCharCount = state.Answer.Length })
        .AddEdge("AnswerQuestion", "SummarizeAnswer")
        .Compile();

// The SDK's own retries are off, as design/44 advises: the node's RecoveryPolicy does the
// retrying, and it sees the provider's retry hint.
static IChatClient CreateOpenAIClient(string model, string apiKey) =>
    new OpenAI.Chat.ChatClient(
        model,
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { RetryPolicy = new ClientRetryPolicy(maxRetries: 0) })
    .AsIChatClient();

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record LlmStreamState
{
    public required string Question { get; init; }
    public string Answer { get; init; } = string.Empty;
    public int AnswerCharCount { get; init; }
}
