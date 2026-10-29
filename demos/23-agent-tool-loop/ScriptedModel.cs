using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Datalium.Strata.Demos.AgentToolLoop;

/// <summary>
/// What the stand-in model says in one turn: text, tool calls, or both. A call's arguments are JSON text, as a
/// provider sends them.
/// </summary>
public sealed record Reply(string Text, IReadOnlyList<(string Id, string Name, string Arguments)> Calls)
{
    public static Reply Say(string text) => new(text, []);

    public static Reply Call(params (string Id, string Name, string Arguments)[] calls) => new(string.Empty, calls);
}

/// <summary>One request the agent sent the model, and the usage the model reported for it.</summary>
public sealed record Request(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, long InputTokens, long OutputTokens);

/// <summary>
/// A stand-in for a real model, so the demo runs offline: an <see cref="IChatClient"/> that plays one scripted turn
/// per request, in order, and records every request. A turn is a function of the conversation so far, so it can read
/// the tool results the agent sent back (an order's total, a refund id) the way a model would.
/// </summary>
/// <remarks>
/// Nothing here is intelligent: the script is the model. The agent can't tell, which is the point: it only sees
/// <see cref="IChatClient"/>. Demo 03 runs the same kind of loop against a real OpenAI model.
/// </remarks>
public sealed class ScriptedModel(params Func<IReadOnlyList<ChatMessage>, Reply>[] turns) : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, Reply>> _turns = new(turns);

    public List<Request> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var conversation = messages.ToList();
        var turn = _turns.Count > 0 ? _turns.Dequeue() : throw new InvalidOperationException("the script has no more turns");
        var reply = turn(conversation);

        // Made-up but deterministic token counts: 40 per message read, 10 per answer plus 15 per tool call.
        var (input, output) = (40L * conversation.Count, 10L + (15L * reply.Calls.Count) + (reply.Text.Length / 4));
        Requests.Add(new Request(conversation, options, input, output));

        List<AIContent> contents = reply.Text.Length == 0 ? [] : [new TextContent(reply.Text)];
        contents.AddRange(reply.Calls.Select(c => FunctionCallContent.CreateFromParsedArguments(
            c.Arguments, c.Id, c.Name, static json => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!)));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = options?.ModelId ?? "support-model-stand-in",
            FinishReason = reply.Calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output },
        });
    }

    // StrataChat streams only when the run does (RunStreaming); this demo never does, but a client should answer both.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", null, "support-model-stand-in") : null;

    public void Dispose()
    {
    }

    /// <summary>The text the agent sent back for the tool call <paramref name="callId"/>.</summary>
    public static string ToolResult(IReadOnlyList<ChatMessage> conversation, string callId) =>
        conversation.Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Single(r => r.CallId == callId).Result as string
        ?? throw new InvalidOperationException($"no result for {callId}");
}
