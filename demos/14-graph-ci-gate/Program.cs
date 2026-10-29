// `strata graph validate` as a CI gate (STRATA-SPEC-004's other entry point).
//
// .Compile() rejects a structurally broken graph at startup -- but that is a check you only get by
// *running* the program. CI wants the same verdict without running anything, from a file:
// `strata graph validate --path graphs/`.
//
// The path from code to that file is StateGraph.ToDefinition(), which exports a graph's structure
// WITHOUT validating it. (.Compile() can't be the export step: it refuses to hand back a broken
// graph, and a broken graph is exactly what the gate exists to catch.)
//
// Seven graphs live in one "repo": four healthy ones and three that each break a different
// STRATA-SPEC-004 rule. What the scenarios show:
//
//   1. A broken pull request -- all seven are exported to graphs/*.json and the real `strata` binary
//      checks the folder in one call. It reports every file, names each error, and exits non-zero.
//      The demo also asserts that for every graph the CLI and .Compile() return the *same*
//      diagnostics.
//   2. A trap the export avoids -- a compiled graph's Topology holds only the edges the executor
//      routes over, so a node reachable only through a recovery policy's fallback looks like an
//      orphan. ToDefinition() adds the fallback route, so the CLI agrees with .Compile().
//   3. An interrupt boundary in the file -- payout-approval's Approve node is a boundary with a
//      reject route. The export marks it (`"interrupt"`, schema_version 1.6.0, design/48) and
//      `strata graph view` says so.
//   4. The fix -- the same seven graphs with three small changes: exit 0, and all seven really do
//      compile.
//   5. When the gate can't check -- a file that isn't a graph, a hand edit that leaves a reject
//      route on a node that is no longer a boundary, an empty folder, a mistyped path. All exit 2,
//      never a silent pass.
//
// The `strata` CLI is a Rust binary, downloaded from the datalium/strata GitHub Release: the demo runs
// the one STRATA_BIN names, else `strata` on the PATH. Pass `--out <dir>` to keep the final
// export, for diffing against the Rust twin's (strata-demos-rust): it writes all seven
// graphs, byte for byte.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Datalium.Strata.Graph;
using Datalium.Strata.Storage.Schema;
using Datalium.Strata.Validation;

var outIndex = Array.IndexOf(args, "--out");
var keep = outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : null;

var root = Path.Combine(Path.GetTempPath(), $"strata-graph-gate-{Environment.ProcessId}");
if (Directory.Exists(root))
{
    Directory.Delete(root, recursive: true);
}

Directory.CreateDirectory(root);
var graphs = Path.Combine(root, "graphs");

Gate.PrepareCli();

// 1. A broken pull request ------------------------------------------------------------------------
Console.WriteLine("== 1. A broken pull request: seven graphs exported, one CLI call ==\n");
Repo.ExportAll(graphs, fixedVersion: false);
Console.WriteLine("$ strata graph validate --path graphs");
var broken = Gate.Run(root, "graphs");
Console.Write(Indent(broken.Human));
Console.WriteLine($"  (exit code {broken.Code})\n");
Check(broken.Code == 1, "a PR with structural errors must fail the gate");
Check((int?)broken.Json?["checked"] == 7 && (int?)broken.Json?["invalid_count"] == 3, "expected 3 of 7 invalid");

Console.WriteLine("The CLI runs the same check as .Compile() -- same diagnostics, every graph:\n");
Console.WriteLine($"  {"graph",-17} {".Compile()",-29} strata graph validate");
foreach (var (name, build) in Repo.Graphs)
{
    var expected = Repo.Broken.Where(b => b.Name == name).Select(b => b.Code).ToList();
    var fromCompile = Repo.CompileCodes(build(false));
    var fromCli = broken.CodesFor(name);
    Check(fromCompile.SequenceEqual(expected), $"{name}: .Compile() gave [{string.Join(",", fromCompile)}]");
    Check(fromCli.SequenceEqual(expected), $"{name}: strata graph validate gave [{string.Join(",", fromCli)}]");
    Console.WriteLine($"  {name,-17} {Shown(fromCompile),-29} {Shown(fromCli)}");
}

// 2. A trap the export avoids ---------------------------------------------------------------------
Console.WriteLine("\n== 2. Why export with ToDefinition(), not the compiled Topology ==\n");
Console.WriteLine("order-pipeline's ManualReview node is reachable only through Charge's");
Console.WriteLine("fallback. A compiled graph's Topology holds just the edges the executor");
Console.WriteLine("routes over, so it has no edge into ManualReview:\n");
var compiled = Repo.OrderPipeline(true).Compile();
var trap = Path.Combine(root, "trap");
Directory.CreateDirectory(trap);
File.WriteAllText(Path.Combine(trap, "routing-edges-only.json"), Repo.ToJson(compiled.Topology.ToDefinition()));
File.WriteAllText(Path.Combine(trap, "to-definition.json"), Repo.ToJson(compiled.ToDefinition()));
var trapReport = Gate.Run(root, "trap");
Console.Write(Indent(trapReport.Human));
Check(trapReport.CodesFor("routing-edges-only").SequenceEqual(["STRATA_ERR_ORPHAN"]), "routing edges alone should look orphaned");
Check(trapReport.CodesFor("to-definition").Count == 0, "ToDefinition() should be clean");
Console.WriteLine("\n  a false alarm on a graph .Compile() accepted; ToDefinition() adds the");
Console.WriteLine("  fallback as a guarded `on_failure` edge, so both agree.");

// 3. An interrupt boundary in the file -------------------------------------------------------------
Console.WriteLine("\n== 3. An interrupt boundary is in the file, with its reject route ==\n");
var payout = JsonNode.Parse(File.ReadAllText(Path.Combine(graphs, "payout-approval.json")))!;
var approve = payout["nodes"]!.AsArray().Single(n => (string?)n!["name"] == "Approve")!;
var rejectEdge = payout["edges"]!.AsArray().Single(e => (string?)e!["condition"] == "on_reject")!;
Console.WriteLine($"payout-approval.json is schema_version {payout["schema_version"]}: a level with a boundary.");
Console.WriteLine("The Approve node, and the route a rejection takes, as exported:\n");
Console.WriteLine($"  {approve.ToJsonString()}");
Console.WriteLine($"  {rejectEdge.ToJsonString()}");
Check((string?)payout["schema_version"] == "1.6.0", "a level with an interrupt boundary is schema_version 1.6.0");
Check(approve["interrupt"] is JsonObject, "the boundary node carries `interrupt`");
Check((string?)rejectEdge["to"] == "Refuse" && (bool?)rejectEdge["guarded"] == true, "the reject route is a guarded edge to Refuse");

Console.WriteLine("\n$ strata graph view --path graphs/payout-approval.json");
var view = Gate.View(root, Path.Combine("graphs", "payout-approval.json"));
Console.Write(Indent(view.Stdout));
Check(view.ExitCode == 0, "graph view should read the file");
Check(view.Stdout.Contains("Approve: interrupt boundary, rejects to Refuse"), "graph view should name the boundary and its route");
Console.WriteLine("\n  Refuse has no ordinary edge in. Like ManualReview, it is reachable only through");
Console.WriteLine("  a route, and the exported on_reject edge is what keeps it from looking orphaned.");

// 4. The fix --------------------------------------------------------------------------------------
Console.WriteLine("\n== 4. The fix: three small changes, same seven files ==\n");
Repo.ExportAll(graphs, fixedVersion: true);
Console.WriteLine("$ strata graph validate --path graphs");
var fixedReport = Gate.Run(root, "graphs");
Console.Write(Indent(fixedReport.Human));
Console.WriteLine($"  (exit code {fixedReport.Code})");
Check(fixedReport.Code == 0, "the fixed graphs should pass the gate");
foreach (var (name, build) in Repo.Graphs)
{
    Check(Repo.CompileCodes(build(true)).Count == 0, $"{name} should compile once fixed");
}

Console.WriteLine("\nAll seven also compile -- the gate said yes to graphs the runtime accepts.");

// 5. When the gate can't check --------------------------------------------------------------------
Console.WriteLine("\n== 5. When the gate can't check something, it doesn't pass ==\n");
File.WriteAllText(Path.Combine(graphs, "notes.json"), "{ \"todo\": \"add more graphs\" }");
var unreadable = Gate.Run(root, "graphs");
Console.WriteLine("a file in graphs/ that isn't a graph definition:");
Console.Write(Indent(unreadable.Human));
Console.WriteLine($"  (exit code {unreadable.Code})");
Check(unreadable.Code == 2 && (int?)unreadable.Json?["unreadable_count"] == 1, "a non-graph file must exit 2");
File.Delete(Path.Combine(graphs, "notes.json"));

// Someone edits the file by hand and drops Approve's `interrupt`, but not its reject route. At a 1.6.0
// level that is a shape no engine writes: the CLI refuses the file (exit 2), and .NET's ToTopology()
// refuses it with the same complaint.
var edited = Path.Combine(root, "edited");
Directory.CreateDirectory(edited);
var handEdit = JsonNode.Parse(File.ReadAllText(Path.Combine(graphs, "payout-approval.json")))!;
handEdit["nodes"]!.AsArray().Single(n => (string?)n!["name"] == "Approve")!.AsObject().Remove("interrupt");
File.WriteAllText(Path.Combine(edited, "payout-approval.json"), handEdit.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
var shape = Gate.Run(root, "edited");
Console.WriteLine("\na hand edit that drops Approve's `interrupt` but keeps its on_reject edge:");
Console.Write(Indent(shape.Human));
Console.WriteLine($"  (exit code {shape.Code})");
Check(shape.Code == 2 && (int?)shape.Json?["unreadable_count"] == 1, "a boundary-less on_reject edge must exit 2");
try
{
    handEdit.Deserialize<GraphDefinition>()!.ToTopology();
    throw new InvalidOperationException("demo assertion failed: ToTopology() should refuse the hand edit");
}
catch (FormatException e)
{
    Console.WriteLine($"  .NET's GraphDefinition.ToTopology() agrees: {e.Message}");
    Check(e.Message.Contains("on_reject") && e.Message.Contains("Approve"), "ToTopology() should name the stray on_reject edge");
}

Directory.CreateDirectory(Path.Combine(root, "empty"));
var empty = Gate.Run(root, "empty");
Console.WriteLine($"\nan empty folder:\n  {empty.Stderr}  (exit code {empty.Code})");
Check(empty.Code == 2, "an empty folder must exit 2");

var typo = Gate.Run(root, "graphz");
Console.WriteLine($"\na mistyped path:\n  {typo.Stderr}  (exit code {typo.Code})");
Check(typo.Code == 2, "a mistyped path must exit 2");

Console.WriteLine("\nExit codes: 0 every graph valid | 1 structural errors | 2 could not check.");

if (keep is not null)
{
    Directory.CreateDirectory(keep);
    foreach (var (name, _) in Repo.Graphs)
    {
        File.Copy(Path.Combine(graphs, $"{name}.json"), Path.Combine(keep, $"{name}.json"), overwrite: true);
    }

    Console.WriteLine($"\nFixed export kept in {Path.GetFullPath(keep)} for diffing against the other runtime.");
}

Directory.Delete(root, recursive: true);
return;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"demo assertion failed: {message}");
    }
}

static string Shown(IReadOnlyList<string> codes) => codes.Count == 0 ? "ok" : string.Join(",", codes);

static string Indent(string text) =>
    string.Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"  {line.TrimEnd('\r')}\n"));

/// <summary>One state type for every graph: nothing here ever runs, only the shape matters.</summary>
internal sealed record Run(int Attempt);

internal static class Repo
{
    /// <summary>Builds a graph. The flag picks the broken (false) or fixed (true) version; the healthy graphs ignore it.</summary>
    public static readonly (string Name, Func<bool, StateGraph<Run>> Build)[] Graphs =
    [
        ("order-pipeline", OrderPipeline),
        ("document-review", DocumentReview),
        ("support-desk", SupportDesk),
        ("refund-flow", RefundFlow),
        ("report-builder", ReportBuilder),
        ("import-job", ImportJob),
        ("payout-approval", PayoutApproval),
    ];

    /// <summary>The broken versions' diagnostics: one STRATA-SPEC-004 rule each.</summary>
    public static readonly (string Name, string Code)[] Broken =
    [
        ("refund-flow", "STRATA_ERR_UNGUARDED_CYCLE"),
        ("report-builder", "STRATA_ERR_ORPHAN"),
        ("import-job", "STRATA_ERR_MISSING_ENTRY"),
    ];

    private static Run Node(Run run) => run;

    /// <summary>ManualReview is reachable only through Charge's fallback: no ordinary edge enters it.</summary>
    public static StateGraph<Run> OrderPipeline(bool fixedVersion) =>
        new StateGraph<Run>()
            .AddNode("Validate", Node)
            .AddFallibleNode("Charge", Node)
            .AddNode("Ship", Node)
            .AddNode("ManualReview", Node)
            .AddEdge("Validate", "Charge")
            .AddEdge("Charge", "Ship")
            .SetRecoveryPolicy("Charge", RecoveryPolicy.Retry(2).WithFallback("ManualReview"));

    /// <summary>A retry loop that is bounded by a runtime predicate.</summary>
    public static StateGraph<Run> DocumentReview(bool fixedVersion) =>
        new StateGraph<Run>()
            .AddNode("Draft", Node)
            .AddNode("Review", Node)
            .AddNode("Publish", Node)
            .AddEdge("Draft", "Review")
            .AddConditionalEdge("Review", "Draft", s => s.Attempt < 3)
            .AddEdge("Review", "Publish");

    /// <summary>A supervisor branching to two agents that rejoin.</summary>
    public static StateGraph<Run> SupportDesk(bool fixedVersion) =>
        new StateGraph<Run>()
            .AddNode("Triage", Node)
            .AddNode("Billing", Node)
            .AddNode("Tech", Node)
            .AddNode("Close", Node)
            .AddConditionalEdge("Triage", "Billing", s => s.Attempt == 0)
            .AddEdge("Triage", "Tech")
            .AddEdge("Billing", "Close")
            .AddEdge("Tech", "Close");

    /// <summary>
    /// A human approves a payout. Approve is an interrupt boundary: a rejection continues at Refuse, which
    /// no ordinary edge enters. The export writes `interrupt` on Approve and the route as an `on_reject` edge.
    /// </summary>
    public static StateGraph<Run> PayoutApproval(bool fixedVersion) =>
        new StateGraph<Run>()
            .AddNode("Prepare", Node)
            .AddNode("Approve", Node)
            .AddNode("Pay", Node)
            .AddNode("Refuse", Node)
            .AddEdge("Prepare", "Approve")
            .AddEdge("Approve", "Pay")
            .AddInterruptBoundary("Approve", onReject: "Refuse");

    /// <summary>Broken: Approve loops back to Request on a plain edge, so nothing bounds the loop. Fixed: the edge gets a predicate.</summary>
    public static StateGraph<Run> RefundFlow(bool fixedVersion)
    {
        var graph = new StateGraph<Run>()
            .AddNode("Request", Node)
            .AddNode("Approve", Node)
            .AddNode("Payout", Node)
            .AddEdge("Request", "Approve");
        graph = fixedVersion
            ? graph.AddConditionalEdge("Approve", "Request", s => s.Attempt < 2)
            : graph.AddEdge("Approve", "Request");
        return graph.AddEdge("Approve", "Payout");
    }

    /// <summary>Broken: Archive is added but nothing leads to it. Fixed: Render does.</summary>
    public static StateGraph<Run> ReportBuilder(bool fixedVersion)
    {
        var graph = new StateGraph<Run>()
            .AddNode("Collect", Node)
            .AddNode("Render", Node)
            .AddNode("Archive", Node)
            .AddEdge("Collect", "Render");
        return fixedVersion ? graph.AddEdge("Render", "Archive") : graph;
    }

    /// <summary>Broken: the entry point names a node that doesn't exist. Fixed: it doesn't.</summary>
    public static StateGraph<Run> ImportJob(bool fixedVersion)
    {
        var graph = new StateGraph<Run>()
            .AddNode("Fetch", Node)
            .AddNode("Store", Node)
            .AddEdge("Fetch", "Store");
        return fixedVersion ? graph : graph.SetEntryPoint("Load");
    }

    /// <summary>The error codes .Compile() reports for a graph; empty if it compiles.</summary>
    public static List<string> CompileCodes(StateGraph<Run> graph)
    {
        try
        {
            graph.Compile();
            return [];
        }
        catch (GraphCompilationException ex)
        {
            return [.. ex.Result.Errors.Select(e => e.Code)];
        }
    }

    // LF newlines and 2-space indent, so the shared graphs' files are byte-identical to the Rust twin's.
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NewLine = "\n" };

    public static string ToJson(GraphDefinition definition) => JsonSerializer.Serialize(definition, Options) + "\n";

    /// <summary>Writes every graph's export to dir/name.json, without compiling it.</summary>
    public static void ExportAll(string dir, bool fixedVersion)
    {
        Directory.CreateDirectory(dir);
        foreach (var (name, build) in Graphs)
        {
            File.WriteAllText(Path.Combine(dir, $"{name}.json"), ToJson(build(fixedVersion).ToDefinition()));
        }
    }
}

/// <summary>What CI sees: the exit code, the human report, and the JSON report.</summary>
internal sealed record GateReport(int Code, string Human, string Stderr, JsonNode? Json)
{
    /// <summary>The error codes the CLI reported for name.json.</summary>
    public List<string> CodesFor(string name)
    {
        var entry = Json?["graphs"]?.AsArray()
            .FirstOrDefault(g => Path.GetFileNameWithoutExtension(g!["path"]!.GetValue<string>()) == name)
            ?? throw new InvalidOperationException($"the CLI report has no entry for {name}");
        return [.. entry["errors"]?.AsArray().Select(e => e!["code"]!.GetValue<string>()) ?? []];
    }
}

internal static class Gate
{
    private const string ReleaseUrl = "https://github.com/datalium/strata/releases/tag/v0.9.0-preview.1";

    private static string? cli;

    /// <summary>Finds the CLI: STRATA_BIN if set, else `strata` on the PATH. Prints its version.</summary>
    public static void PrepareCli()
    {
        cli = Environment.GetEnvironmentVariable("STRATA_BIN") is { Length: > 0 } bin ? bin : OnPath("strata");
        if (cli is null)
        {
            throw new InvalidOperationException(
                $"no strata CLI on the PATH. Download the binary for your platform from {ReleaseUrl}, "
                + "put it on the PATH as `strata` (`strata.exe` on Windows), or set STRATA_BIN to it.");
        }

        var version = Start(Directory.GetCurrentDirectory(), cli, ["--version"]);
        Console.WriteLine($"Using strata CLI: {cli} ({version.Stdout.Trim()})\n");
    }

    private static string? OnPath(string name)
    {
        var names = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : [name];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(n => Path.Combine(dir, n)))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>`strata graph view` on one file.</summary>
    public static (int ExitCode, string Stdout, string Stderr) View(string cwd, string path) =>
        Strata(cwd, ["graph", "view", "--path", path]);

    /// <summary>`strata graph validate` run from <paramref name="cwd"/>, so paths in its report are short and relative.</summary>
    public static GateReport Run(string cwd, string path)
    {
        var human = Strata(cwd, ["graph", "validate", "--path", path]);
        var json = Strata(cwd, ["graph", "validate", "--json", "--path", path]);
        JsonNode? parsed = null;
        try
        {
            parsed = JsonNode.Parse(json.Stdout);
        }
        catch (JsonException)
        {
            // Nothing on stdout: the CLI could not check the path at all.
        }

        return new GateReport(human.ExitCode, human.Stdout, human.Stderr.Trim(), parsed);
    }

    private static (int ExitCode, string Stdout, string Stderr) Strata(string cwd, string[] args) =>
        Start(cwd, cli ?? throw new InvalidOperationException("call PrepareCli first"), args);

    private static (int ExitCode, string Stdout, string Stderr) Start(string cwd, string file, string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {file}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
