using Datalium.Strata.Tools;

namespace Datalium.Strata.Demos.LlmToolCalling;

/// <summary>
/// The same <c>add</c> tool as <c>02-tool-calling</c>'s <c>CalculatorTools</c> (here it logs the agent's ids): STRATA-SPEC-003 tool-calling,
/// with <c>Datalium.Strata.SourceGenerators</c> generating the <see cref="IStrataToolProvider"/>
/// implementation (JSON-Schema manifest + dispatch switch). What's new in this demo is who
/// decides to call it -- see <c>Program.cs</c>.
/// </summary>
public partial class CalculatorTools
{
    /// <summary>
    /// <paramref name="a"/> and <paramref name="b"/> are what the model sends.
    /// <paramref name="context"/> is filled by the agent that dispatches the call: its
    /// <see cref="ToolCallContext.CallerId"/> is the agent's id and its
    /// <see cref="ToolCallContext.CallId"/> the model's id for the call (design/44-LLM-Integration.md).
    /// </summary>
    [StrataTool(Name = "add", Description = "Adds two numbers and returns their sum.")]
    public static async Task<double> AddAsync(double a, double b, ToolCallContext context)
    {
        Console.WriteLine($"[add] {a} + {b}, called by agent '{context.CallerId}' (model call id {context.CallId}) in node '{context.NodePath}'");
        return await Task.FromResult(a + b);
    }
}
