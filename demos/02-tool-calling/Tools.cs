using Datalium.Strata.Tools;

namespace Datalium.Strata.Demos.ToolCalling;

/// <summary>
/// STRATA-SPEC-003 tool-calling: <c>Datalium.Strata.SourceGenerators</c> generates a second
/// partial declaration of this class implementing <see cref="IStrataToolProvider"/> -- a
/// compile-time JSON-Schema manifest plus a dispatch switch calling <see cref="AddAsync"/>
/// directly, no reflection.
/// </summary>
public partial class CalculatorTools
{
    /// <summary>
    /// <paramref name="a"/> and <paramref name="b"/> are the tool's JSON arguments.
    /// <paramref name="context"/> is not: the generator leaves a <see cref="ToolCallContext"/>
    /// parameter out of the manifest and fills it from the dispatch call
    /// (design/20-Tool-Dispatch-And-Registry.md).
    /// </summary>
    [StrataTool(Name = "add", Description = "Adds two numbers and returns their sum.")]
    public static async Task<double> AddAsync(double a, double b, ToolCallContext context)
    {
        // An audit line needs to know who called. The two-argument DispatchToolAsync passes
        // ToolCallContext.None, so a call made that way is refused rather than logged as anonymous.
        if (context.ThreadId is null || context.NodePath is null)
        {
            throw new InvalidOperationException("add needs a caller: dispatch it with a ToolCallContext");
        }

        Console.WriteLine($"[add] called by node '{context.NodePath}' on thread '{context.ThreadId}', attempt {context.Attempt}");
        return await Task.FromResult(a + b);
    }
}
