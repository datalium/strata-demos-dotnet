// Pluggable storage: one graph, one scenario, four storage backends.
//
// design/03-Architecture.md promises the persistence layer is pluggable: ICheckpointStore and
// IHistoryStore are interfaces, and "in-memory -> SQLite -> Redis/PostgreSQL" are just
// implementations. Until this demo only SQLite existed, so "pluggable" meant "two SQL-ish things".
// This demo runs the *same* human-in-the-loop scenario -- freeze a wire-transfer graph, drop every
// connection, resume it from a brand-new one, keep a chat history alongside -- against in-memory,
// SQLite, Postgres and Redis, through the same code: nothing in RunScenarioAsync knows which backend
// it is talking to.
//
// Redis is the interesting one. It has no tables and no MAX(sequence) + 1: a thread's checkpoint log
// and a session's history are each one Redis list. That the interfaces admit it unchanged is the
// actual test of "pluggable".
//
// After the matrix, one more in-process check on the three durable backends: a durable graph's
// thread written by a store with delta checkpoints on (design/41) and read back, whole, by a store
// opened without them.
//
//   docker compose up -d --wait        # Postgres + Redis (this repo's docker-compose.yml)
//   dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj   # the matrix
//
// Cross-runtime. The Postgres and Redis layouts are shared with the Rust stores, so the freeze/resume
// hand-off of demo 09 works through them too:
//
//   dotnet run --project demos/12-pluggable-storage/12-pluggable-storage.csproj -- freeze postgres   # .NET freezes ...
//   cargo run -p pluggable-storage -- resume postgres   # (in ../strata-demos-rust)             # ... Rust resumes
//
// Modes: [matrix [backend...]], freeze <postgres|redis>, resume <postgres|redis>. Connection URLs come
// from STRATA_DEMO_POSTGRES_URL / STRATA_DEMO_REDIS_URL (defaults match the compose file). The Rust twin
// of this file is demos/12-pluggable-storage/src/main.rs in the sibling repo; the two must agree on
// everything the freeze/resume modes put on the wire.

using System.Diagnostics.Metrics;
using System.Text.Json.Serialization;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Memory;
using Datalium.Strata.Storage;
using Datalium.Strata.Storage.Memory;
using Datalium.Strata.Storage.Postgres;
using Datalium.Strata.Storage.Redis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

const string runtime = "dotnet";
// The thread/session, and Redis key prefix, the cross-runtime freeze/resume modes share with the Rust twin.
const string sharedId = "wire-transfer-pluggable";
const string sharedRedisPrefix = "strata-demo-12";

// The in-process deltas check's graph: this many nodes, one checkpoint after each.
const int LedgerSteps = 6;

try
{
    var mode = args.Length > 0 ? args[0] : "matrix";
    switch (mode)
    {
        case "matrix":
            await MatrixAsync(args.Skip(1).ToArray());
            return 0;
        case "freeze":
            await FreezeAsync(SharedBackend(args.ElementAtOrDefault(1)));
            return 0;
        case "resume":
            await ResumeAsync(SharedBackend(args.ElementAtOrDefault(1)));
            return 0;
        default:
            Console.Error.WriteLine(
                "usage: 12-pluggable-storage [matrix [memory|sqlite|postgres|redis ...]]\n" +
                "       12-pluggable-storage <freeze|resume> <postgres|redis>");
            return 2;
    }
}
catch (DemoException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static WireTransferState InitialState() => new(
    InvoiceGenerated: false,
    Amount: 4200.50,
    Currency: "USD",
    ApprovedBy: null,
    IsCompleted: false,
    FrozenBy: null,
    ExecutedBy: null);

static CompiledGraph<WireTransferState> WireTransferGraph() =>
    new StateGraph<WireTransferState>()
        .AddNode("PrepareWireTransfer", s => s with { InvoiceGenerated = true, FrozenBy = runtime })
        .AddInterruptBoundary("PrepareWireTransfer")
        .AddNode("ExecuteWireTransfer", s => s with { IsCompleted = true, ExecutedBy = runtime })
        .AddEdge("PrepareWireTransfer", "ExecuteWireTransfer")
        .Compile();

// The override is keyed by the *JSON* name (approved_by), not the C# property name: it is merged into
// the frozen state's JSON before that is rehydrated into WireTransferState (see demo 09).
static HumanSignal Approval() => new(
    IsApproved: true,
    ReviewerToken: "token-xyz",
    FieldOverrides: new Dictionary<string, string> { ["approved_by"] = "compliance_officer_07" });

static ChatMessage Message(MessageRole role, string text) => new(role, text.AsMemory(), DateTimeOffset.UtcNow);

static string PostgresUrl() =>
    Environment.GetEnvironmentVariable("STRATA_DEMO_POSTGRES_URL") ?? "postgres://strata:strata@localhost:5432/strata";

static string RedisUrl() =>
    Environment.GetEnvironmentVariable("STRATA_DEMO_REDIS_URL") ?? "redis://localhost:6379";

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new DemoException($"check failed: {what}");
    }
}

static async Task<List<ChatMessage>> ReadHistoryAsync(IHistoryStore history, string session, int limit = 50)
{
    var messages = new List<ChatMessage>();
    await foreach (var message in history.GetHistoryAsync(session, limit))
    {
        messages.Add(message);
    }

    return messages;
}

// --- The scenario: identical for every backend -----------------------------------------------------

// Freeze -> "process exit" -> resume from a fresh connection -> read back. Knows nothing about which
// backend `backend` is beyond the questions it asks through Backend's members.
static async Task<Row> RunScenarioAsync(Backend backend, string runId)
{
    var id = $"wire-{runId}-{backend.Name}";

    // Process 1: run to the interrupt boundary and chat a little.
    var first = await backend.OpenAsync();
    var frozen = await WireTransferGraph().RunAsync(id, InitialState(), first.Checkpoints);
    if (frozen is not GraphExecutionResult<WireTransferState>.Interrupted)
    {
        throw new DemoException($"expected Interrupted, got {frozen}");
    }

    await first.History.AppendAsync(id, Message(MessageRole.User, "Wire $4,200.50 to Acme Corp"));
    await first.History.AppendAsync(id, Message(MessageRole.Assistant, "Prepared the transfer; waiting for approval."));

    // "The process exits." A durable backend hands us nothing but its address; the in-memory one takes
    // its data with it.
    Stores second;
    if (backend.Durable)
    {
        await first.DisposeAsync();
        second = await backend.OpenAsync();
    }
    else
    {
        await using var restarted = await backend.OpenAsync();
        Check(await restarted.Checkpoints.LoadAsync(id) is null, "a restarted in-memory store should have lost the checkpoint");
        second = first;
    }

    // Process 2: resume from the store alone, using a graph built from scratch.
    var resumed = await WireTransferGraph().ResumeAsync(id, Approval(), second.Checkpoints);
    if (resumed is not GraphExecutionResult<WireTransferState>.Completed completed)
    {
        throw new DemoException($"expected Completed on resume, got {resumed}");
    }

    Check(
        completed.State.IsCompleted && completed.State.ApprovedBy == "compliance_officer_07",
        "the resumed state carries the human's approval");
    await second.History.AppendAsync(id, Message(MessageRole.Assistant, "Transfer executed."));

    // Process 3: read everything back through yet another connection.
    Stores third;
    if (backend.Durable)
    {
        await second.DisposeAsync();
        third = await backend.OpenAsync();
    }
    else
    {
        third = second;
    }

    await using (third)
    {
        var latest = await third.Checkpoints.LoadAsync(id) ?? throw new DemoException("no checkpoint after resume");
        Check(
            latest.ResumedAt is not null && latest.Status == CheckpointStatus.Interrupted,
            "the latest checkpoint is the resumed one");

        var history = await ReadHistoryAsync(third.History, id);
        Check(
            history.Select(m => m.Role).SequenceEqual([MessageRole.User, MessageRole.Assistant, MessageRole.Assistant])
                && history[2].Content.ToString() == "Transfer executed.",
            "history comes back complete and in order");

        // A resumed thread cannot be resumed twice, whichever backend holds it.
        var again = await WireTransferGraph().ResumeAsync(id, Approval(), third.Checkpoints);
        Check(again is not GraphExecutionResult<WireTransferState>.Completed, "a second resume is refused");

        string concurrent;
        if (backend.MultiWriter)
        {
            var appended = await ConcurrentAppendsAsync(backend, $"{id}-concurrent");
            concurrent = $"{appended} appends, none lost";
        }
        else
        {
            concurrent = backend.Durable ? "single writer assumed" : "in-process only";
        }

        return new Row(backend.Name, backend.Durable, history.Count, concurrent);
    }
}

// Four connections append to the same session at once. Postgres serializes them with a per-session
// advisory lock and Redis with an atomic RPUSH; either way nothing is lost and each writer's messages
// stay in its own order.
static async Task<int> ConcurrentAppendsAsync(Backend backend, string session)
{
    const int writers = 4;
    const int each = 10;
    var stores = await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => backend.OpenAsync()));
    try
    {
        await Task.WhenAll(stores.Select((s, writer) => Task.Run(async () =>
        {
            for (var n = 0; n < each; n++)
            {
                await s.History.AppendAsync(session, Message(MessageRole.User, $"{writer}:{n}"));
            }
        })));

        var history = await ReadHistoryAsync(stores[0].History, session, writers * each + 10);
        Check(history.Count == writers * each, "no concurrent append was lost");
        for (var writer = 0; writer < writers; writer++)
        {
            var mine = history
                .Select(m => m.Content.ToString())
                .Where(c => c.StartsWith($"{writer}:", StringComparison.Ordinal))
                .Select(c => int.Parse(c.Split(':')[1]));
            Check(mine.SequenceEqual(Enumerable.Range(0, each)), "each writer's messages stay in order");
        }

        return history.Count;
    }
    finally
    {
        foreach (var s in stores)
        {
            await s.DisposeAsync();
        }
    }
}

// --- Deltas: one durable thread, written with deltas, read back without ---------------------------

// Six nodes, each adding one entry to a state that also carries 2 KB of terms that never change.
// Compiled with CheckpointPolicy.EveryNode (design/33), so the run writes a `running` checkpoint
// before its first node and after every node that leads on, and a `completed` one after the last:
// seven rows. The first is whole (the store has nothing to diff against), the next five are deltas,
// and `completed` is whole (only `running` checkpoints are stored as deltas).
static CompiledGraph<LedgerState> LedgerGraph()
{
    var graph = new StateGraph<LedgerState>();
    for (var step = 1; step <= LedgerSteps; step++)
    {
        var entry = $"entry {step}";
        graph.AddNode($"Step{step}", s => s with { Entries = [.. s.Entries, entry] });
        if (step > 1)
        {
            graph.AddEdge($"Step{step - 1}", $"Step{step}");
        }
    }

    return graph.Compile(new CompilationOptions { Checkpointing = CheckpointPolicy.EveryNode });
}

// A store with Deltas on keeps a `running` checkpoint as the keys that changed since the thread's
// previous row (checkpoint 1.8.0 in the table or list). Every store decodes such rows whatever its own
// options, so a reader opened without Deltas gets each checkpoint back whole. The writer's encodings
// are counted from the stores' own telemetry (strata.store.checkpoint.size), which names no backend's
// internals.
static async Task<DeltaRow> RunDeltasAsync(Backend backend, string runId)
{
    var id = $"ledger-{runId}-{backend.Name}";
    using var sizes = new CheckpointSizes(backend.Name);

    await using (var writer = await backend.OpenAsync(new DeltaOptions()))
    {
        var result = await LedgerGraph().RunAsync(id, LedgerState.New(), writer.Checkpoints);
        Check(result is GraphExecutionResult<LedgerState>.Completed, $"the ledger run completes (got {result})");
    }

    await using var reader = await backend.OpenAsync();
    var query = (ICheckpointQuery)reader.Checkpoints;
    var log = (await query.GetLogAsync(id, limit: 50)).Entries;
    var latest = await reader.Checkpoints.LoadAsync(id) ?? throw new DemoException("no checkpoint for the ledger");

    // Each row comes back whole: the unchanged terms, the entries written so far, and the version the
    // engine wrote (1.7.0 for `running`; 1.0.0 for a plain `completed`) rather than the 1.8.0 the store kept.
    var rowsWhole = log.Select((entry, i) => (entry, i)).All(x =>
        x.entry.Checkpoint.SchemaVersion == (x.entry.Checkpoint.Status == CheckpointStatus.Running ? "1.7.0" : "1.0.0")
        && (string?)x.entry.Checkpoint.FrozenState?["terms"] == LedgerState.DefaultTerms
        && x.entry.Checkpoint.FrozenState?["entries"]?.AsArray().Count == Math.Min(x.i, LedgerSteps));
    Check(log.Count == LedgerSteps + 1, $"expected {LedgerSteps + 1} checkpoints, got {log.Count}");
    Check(rowsWhole, "a store without Deltas reads every row back whole");
    Check(latest.Status == CheckpointStatus.Completed, "the latest checkpoint is the completed one");
    Check(sizes.Count("delta") == LedgerSteps - 1 && sizes.Count("plain") == 2, $"expected {LedgerSteps - 1} deltas and 2 whole rows, saw {sizes}");
    Check(sizes.Average("delta") * 4 < sizes.Average("plain"), "a delta is much smaller than a whole row");

    return new DeltaRow(backend.Name, log.Count, sizes.Count("plain"), sizes.Count("delta"), sizes.Average("plain"), sizes.Average("delta"));
}

// --- Modes -----------------------------------------------------------------------------------------

static Backend ParseBackend(string name, string runId) => name switch
{
    "memory" or "in-memory" => new Backend(BackendKind.InMemory, string.Empty, string.Empty),
    "sqlite" => new Backend(BackendKind.Sqlite, Path.Combine(Path.GetTempPath(), $"strata-demo-12-{runId}.sqlite"), string.Empty),
    "postgres" => new Backend(BackendKind.Postgres, PostgresUrl(), string.Empty),
    "redis" => new Backend(BackendKind.Redis, RedisUrl(), $"strata-demo-12-{runId}"),
    _ => throw new DemoException($"unknown backend '{name}' (memory, sqlite, postgres, redis)"),
};

static async Task MatrixAsync(string[] requested)
{
    var runId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    string[] names = requested.Length == 0 ? ["memory", "sqlite", "postgres", "redis"] : requested;

    Console.WriteLine($"=== one scenario, {names.Length} storage backends ===");
    Console.WriteLine(
        "  freeze a wire-transfer graph -> drop every connection -> resume from a fresh one -> read history back\n");

    var rows = new List<Row>();
    foreach (var name in names)
    {
        var backend = ParseBackend(name, runId);
        Console.Write($"  {backend.Name,-10} ");
        try
        {
            rows.Add(await RunScenarioAsync(backend, runId));
            Console.WriteLine("ok");
        }
        catch (DemoException e)
        {
            Console.WriteLine("FAILED");
            throw new DemoException($"{backend.Name}: {e.Message}");
        }
        finally
        {
            if (backend.Kind == BackendKind.Sqlite)
            {
                // Microsoft.Data.Sqlite pools connections, which keeps the file open after a store is disposed.
                SqliteConnection.ClearAllPools();
                File.Delete(backend.Location);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  {"backend",-10} {"survives restart",-22} {"history",-8} concurrent writers");
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"  {row.Backend,-10} {(row.Durable ? "yes" : "no (lost, as expected)"),-22} {row.History,-8} {row.Concurrent}");
    }

    Console.WriteLine();
    Console.WriteLine("  Same graph, same assertions, same code path for all of them.");

    var durable = names.Select(name => ParseBackend(name, runId)).Where(b => b.Durable).ToList();
    if (durable.Count == 0)
    {
        return;
    }

    Console.WriteLine();
    Console.WriteLine("=== delta checkpoints: written with Deltas on, read by a store without them ===");
    Console.WriteLine(
        $"  a durable graph ({LedgerSteps} nodes, CheckpointPolicy.EveryNode) runs once on a store with Deltas; a fresh store\n" +
        "  opened without Deltas reads the thread's log back\n");
    var deltaRows = new List<DeltaRow>();
    foreach (var backend in durable)
    {
        try
        {
            deltaRows.Add(await RunDeltasAsync(backend, runId));
        }
        catch (DemoException e)
        {
            throw new DemoException($"{backend.Name} (deltas): {e.Message}");
        }
        finally
        {
            if (backend.Kind == BackendKind.Sqlite)
            {
                SqliteConnection.ClearAllPools();
                File.Delete(backend.Location);
            }
        }
    }

    Console.WriteLine($"  {"backend",-10} {"rows read",-10} {"stored whole",-13} {"as deltas",-10} bytes (whole / delta, average)");
    foreach (var row in deltaRows)
    {
        Console.WriteLine(
            $"  {row.Backend,-10} {row.Rows,-10} {row.Whole,-13} {row.Deltas,-10} {row.WholeBytes:0} / {row.DeltaBytes:0}");
    }

    Console.WriteLine();
    Console.WriteLine("  Every row came back whole, in order: the reader needed no options to decode the deltas.");
}

static Backend SharedBackend(string? name) => name switch
{
    "postgres" => new Backend(BackendKind.Postgres, PostgresUrl(), string.Empty),
    "redis" => new Backend(BackendKind.Redis, RedisUrl(), sharedRedisPrefix),
    "sqlite" => throw new DemoException(
        "the SQLite hand-off is demo 09; this demo shares Postgres and Redis (postgres|redis)"),
    _ => throw new DemoException("usage: 12-pluggable-storage <freeze|resume> <postgres|redis>"),
};

static void PrintHistory(List<ChatMessage> history)
{
    Console.WriteLine("  History (as read from the store):");
    foreach (var m in history)
    {
        Console.WriteLine($"    {m.Role.ToString().ToLowerInvariant(),-10} {m.Content}");
    }
}

static async Task FreezeAsync(Backend backend)
{
    Console.WriteLine($"=== [{runtime}] freeze on {backend.Name}: run to the interrupt boundary ===");
    await using var stores = await backend.OpenAsync();
    var result = await WireTransferGraph().RunAsync(sharedId, InitialState(), stores.Checkpoints);
    if (result is not GraphExecutionResult<WireTransferState>.Interrupted interrupted)
    {
        throw new DemoException($"expected Interrupted, got {result}");
    }

    Console.WriteLine(
        $"  Interrupted at '{interrupted.SuspendedNode}' (thread '{interrupted.ThreadId}'); next node on resume: '{interrupted.NextNode}'");
    await stores.History.AppendAsync(sharedId, Message(MessageRole.User, "[dotnet] Wire $4,200.50 to Acme Corp"));
    await stores.History.AppendAsync(
        sharedId, Message(MessageRole.Assistant, "[dotnet] Prepared the transfer; waiting for approval."));
    Console.WriteLine("  Appended 2 history messages.");
    Console.WriteLine();
    Console.WriteLine("  This process is about to exit; the state now lives only in the database.");
    Console.WriteLine("  Next: run the `resume` step -- from the other runtime to see the hand-off.");
}

static async Task ResumeAsync(Backend backend)
{
    Console.WriteLine($"=== [{runtime}] resume on {backend.Name}: pick up a thread another process froze ===");
    await using var stores = await backend.OpenAsync();
    var result = await WireTransferGraph().ResumeAsync(sharedId, Approval(), stores.Checkpoints);
    switch (result)
    {
        case GraphExecutionResult<WireTransferState>.Completed completed:
            Console.WriteLine("  Resumed and ran to completion.");
            Console.WriteLine($"  Completed state: {completed.State}");
            Console.WriteLine();
            Console.WriteLine(
                $"  frozen_by = \"{completed.State.FrozenBy ?? "?"}\", executed_by = \"{completed.State.ExecutedBy ?? "?"}\"");
            break;
        case GraphExecutionResult<WireTransferState>.Failed failed:
            throw new DemoException(
                $"resume failed: {failed.Message} (was this thread already resumed? run `freeze` again)");
        default:
            throw new DemoException($"expected Completed, got {result}");
    }

    await stores.History.AppendAsync(sharedId, Message(MessageRole.Assistant, "[dotnet] Transfer executed."));
    Console.WriteLine();
    PrintHistory(await ReadHistoryAsync(stores.History, sharedId));
}

// --- The backends ----------------------------------------------------------------------------------

enum BackendKind
{
    InMemory,
    Sqlite,
    Postgres,
    Redis,
}

sealed class DemoException(string message) : Exception(message);

sealed record Row(string Backend, bool Durable, int History, string Concurrent);

sealed record DeltaRow(string Backend, int Rows, int Whole, int Deltas, double WholeBytes, double DeltaBytes);

sealed record LedgerState(
    [property: JsonPropertyName("terms")] string Terms,
    [property: JsonPropertyName("entries")] IReadOnlyList<string> Entries)
{
    // About 2 KB that no node changes: what a delta doesn't have to store again.
    public static readonly string DefaultTerms = string.Join(' ', Enumerable.Repeat("Payment is due within thirty days of the invoice date.", 38));

    public static LedgerState New() => new(DefaultTerms, []);
}

// What one backend's checkpoint stores report through `strata.store.checkpoint.size` (meter
// Datalium.Strata.Storage): the bytes of each row written, by encoding (plain, delta, ...).
sealed class CheckpointSizes : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Encoding, long Bytes)> _rows = [];

    public CheckpointSizes(string store)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == StorageTelemetry.Name && instrument.Name == "strata.store.checkpoint.size")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, bytes, tags, _) =>
        {
            string? from = null, encoding = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "strata.store")
                {
                    from = tag.Value as string;
                }
                else if (tag.Key == "strata.store.encoding")
                {
                    encoding = tag.Value as string;
                }
            }

            if (from == store && encoding is not null)
            {
                lock (_rows)
                {
                    _rows.Add((encoding, bytes));
                }
            }
        });
        _listener.Start();
    }

    public int Count(string encoding)
    {
        lock (_rows)
        {
            return _rows.Count(r => r.Encoding == encoding);
        }
    }

    public double Average(string encoding)
    {
        lock (_rows)
        {
            var matching = _rows.Where(r => r.Encoding == encoding).ToList();
            return matching.Count == 0 ? 0 : matching.Average(r => r.Bytes);
        }
    }

    public override string ToString()
    {
        lock (_rows)
        {
            return string.Join(", ", _rows.GroupBy(r => r.Encoding).Select(g => $"{g.Key}: {g.Count()}"));
        }
    }

    public void Dispose() => _listener.Dispose();
}

// A checkpoint store and a history store on the same backend.
sealed class Stores(ICheckpointStore checkpoints, IHistoryStore history, params object[] owned) : IAsyncDisposable
{
    public ICheckpointStore Checkpoints { get; } = checkpoints;

    public IHistoryStore History { get; } = history;

    public async ValueTask DisposeAsync()
    {
        foreach (var store in owned)
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }
}

// Where a backend keeps its data. Everything above this line is generic over it; this is the *only*
// place that names a concrete store.
sealed record Backend(BackendKind Kind, string Location, string Prefix)
{
    public string Name => Kind switch
    {
        BackendKind.InMemory => "in-memory",
        BackendKind.Sqlite => "sqlite",
        BackendKind.Postgres => "postgres",
        _ => "redis",
    };

    // Does what one store instance saved outlive it? (Open again and see.)
    public bool Durable => Kind != BackendKind.InMemory;

    // Is more than one writer to the same thread/session supported? SQLite's adapters assume a single
    // writer; Postgres serializes writers with an advisory lock; Redis appends with an atomic RPUSH.
    public bool MultiWriter => Kind is BackendKind.Postgres or BackendKind.Redis;

    // A fresh pair of stores with their own connections -- what a newly started process would have.
    // `deltas` turns on delta checkpoints for the checkpoint store (not the in-memory one, which has none).
    //
    // The Postgres stores' constructors don't touch the network; OpenAsync creates or migrates their tables
    // first, so an unreachable or too-new database fails here, at startup, not at the first save. Both
    // Postgres and Redis stores retry a transient failure inside every call (design/34).
    public async Task<Stores> OpenAsync(DeltaOptions? deltas = null)
    {
        try
        {
            switch (Kind)
            {
                case BackendKind.InMemory:
                    return new Stores(new InMemoryCheckpointStore(), new InMemoryHistoryStore());
                case BackendKind.Sqlite:
                    {
                        var checkpoints = new EfCoreCheckpointStore(
                            new CheckpointDbContext(SqliteOptions<CheckpointDbContext>()),
                            options: new SqliteStoreOptions { Deltas = deltas });
                        var history = new EfCoreHistoryStore(new HistoryDbContext(SqliteOptions<HistoryDbContext>()));
                        return new Stores(checkpoints, history, checkpoints, history);
                    }

                case BackendKind.Postgres:
                    {
                        var checkpoints = await PostgresCheckpointStore.OpenAsync(Location, new PostgresStoreOptions { Deltas = deltas });
                        var history = await PostgresHistoryStore.OpenAsync(Location);
                        return new Stores(checkpoints, history, checkpoints, history);
                    }

                default:
                    {
                        var checkpoints = await RedisCheckpointStore.ConnectAsync(Location, new RedisStoreOptions { Prefix = Prefix, Deltas = deltas });
                        var history = await RedisHistoryStore.ConnectAsync(Location, new RedisStoreOptions { Prefix = Prefix });
                        return new Stores(checkpoints, history, checkpoints, history);
                    }
            }
        }
        catch (Exception e) when (Kind is BackendKind.Postgres or BackendKind.Redis)
        {
            var (service, variable) = Kind == BackendKind.Postgres
                ? ("Postgres", "STRATA_DEMO_POSTGRES_URL")
                : ("Redis", "STRATA_DEMO_REDIS_URL");
            // StackExchange.Redis appends advice about abortConnect after the first sentence.
            var reason = (e.InnerException?.Message ?? e.Message).Split(". ")[0].TrimEnd('.');
            throw new DemoException(
                $"cannot use {service} at {Redact(Location)}: {reason}\n  start it with `docker compose up -d --wait` (or set {variable})");
        }
    }

    // scheme://user:password@host/... with the password masked, for error messages.
    private static string Redact(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        var at = url.LastIndexOf('@');
        if (schemeEnd < 0 || at <= schemeEnd)
        {
            return url;
        }

        var userinfo = url[(schemeEnd + 3)..at];
        var colon = userinfo.IndexOf(':');
        return colon < 0 ? url : $"{url[..(schemeEnd + 3)]}{userinfo[..colon]}:***@{url[(at + 1)..]}";
    }

    private DbContextOptions<TContext> SqliteOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>().UseSqlite($"Data Source={Location}").Options;
}

// Field names are snake_case on the wire, and the Rust twin's struct uses the same names (see demo 09
// for why that matters: the state is user-defined JSON inside the checkpoint).
sealed record WireTransferState(
    [property: JsonPropertyName("invoice_generated")] bool InvoiceGenerated,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("approved_by")] string? ApprovedBy,
    [property: JsonPropertyName("is_completed")] bool IsCompleted,
    [property: JsonPropertyName("frozen_by")] string? FrozenBy,
    [property: JsonPropertyName("executed_by")] string? ExecutedBy);
