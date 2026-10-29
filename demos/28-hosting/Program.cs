// Hosting a Strata graph in the .NET Generic Host (design/35-Hosting-Integration.md).
//
// A reminder service: Schedule -> Wait (a five-minute timer) -> Send. The host wires everything
// through services.AddStrata():
//
//   .AddSqliteCheckpoints()    the store, from Strata:Sqlite:Path in configuration
//   .AddHealthChecks()         strata-checkpoints (can the store answer?) and strata-drivers
//   .AddGraph(...)             the compiled graph, plus a StrataRunner<Reminder> to inject
//   .AddWakeDriver<Reminder>() a hosted loop that wakes threads whose timer is due
//
// Settings come from appsettings.json (the wake driver's interval, the run lease) layered with
// in-memory configuration (the SQLite file, a temp path). The clock is a FakeTimeProvider in the
// container, so a five-minute timer is due as soon as the demo moves the clock: the demo runs in
// a second or two and asserts what happened, never how long it took.
//
// Five scenarios:
//   1. The host starts: the store and the options came from configuration, and both health checks
//      are healthy.
//   2. A thread waits on its timer; once it is due, the wake driver resumes it. Nobody calls
//      WakeDueAsync by hand.
//   3. A clean stop: the host is stopped while a woken run is in flight. The run finishes, nothing
//      is cancelled, and the other due thread is left for the next host, which wakes it.
//   4. An outage under a running host: the checkpoint check goes unhealthy, the driver check
//      degraded, and the host keeps running.
//   5. A graph with a structural error stops the host from starting.
//
// No LLM and no network: the "send" is a list in memory.

using System.Collections.Concurrent;
using Datalium.Strata.Graph;
using Datalium.Strata.Hosting;
using Datalium.Strata.Interruption;
using Datalium.Strata.Storage.Memory;
using Datalium.Strata.Validation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
var dbPath = Path.Combine(Path.GetTempPath(), $"strata-demo-28-{Guid.NewGuid():N}.sqlite");
var logs = new DriverLog();

try
{
    // --- Scenario 1: a host from configuration ---

    Console.WriteLine("=== Scenario 1: the host starts, wired from configuration ===");
    var host = BuildHost(clock, dbPath, logs, _ => ReminderGraph(withOrphan: false));
    await host.StartAsync();

    var store = host.Services.GetRequiredService<ICheckpointStore>();
    var wake = host.Services.GetRequiredService<IOptionsMonitor<WakeDriverOptions>>().Get(typeof(Reminder).FullName);
    var runner = host.Services.GetRequiredService<StrataRunner<Reminder>>();
    var lease = runner.CreateOptions().Lease;
    Console.WriteLine($"  Checkpoint store: {store.GetType().Name} on a temp file (Strata:Sqlite:Path, in-memory configuration)");
    Console.WriteLine($"  Wake driver: every {wake.Interval}, batches of {wake.BatchSize} (Strata:Wake, appsettings.json)");
    Console.WriteLine($"  Run lease: owner '{lease?.Owner}', ttl {lease?.Ttl} (Strata:Runs:Lease, appsettings.json)");
    Check(store is EfCoreCheckpointStore && File.Exists(dbPath), "expected the SQLite store, with its file created at start-up");
    Check(wake.Interval == TimeSpan.FromSeconds(5) && wake.BatchSize == 10, "expected the wake settings from appsettings.json");
    Check(lease is { Owner: "reminders-1" } && lease.Ttl == TimeSpan.FromSeconds(30), "expected the lease from appsettings.json");

    var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
    foreach (var (name, entry) in report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"  Health {name}: {entry.Status}");
    }

    Check(report.Status == HealthStatus.Healthy && report.Entries.Count == 2, "expected two healthy checks");

    // --- Scenario 2: the wake driver resumes a due timer ---

    Console.WriteLine();
    Console.WriteLine("=== Scenario 2: a five-minute timer, woken by the driver ===");
    var desk = host.Services.GetRequiredService<ReminderDesk>();
    var outbox = host.Services.GetRequiredService<Outbox>();
    var first = await desk.ScheduleAsync("remind-alice", "alice", "stand-up at 09:05");
    var waiting = first as GraphExecutionResult<Reminder>.Interrupted
        ?? throw new InvalidOperationException($"expected the run to wait on its timer, got: {first}");
    Console.WriteLine($"  RunAsync returned Interrupted at '{waiting.SuspendedNode}': a {waiting.Wait?.Kind} due {waiting.Wait?.WakeAt}");
    Check(waiting.Wait is { Kind: WaitKind.Timer, WakeAt: "2026-10-24T09:05:00Z" }, "expected a timer due at 09:05");

    // The driver's rounds come and go; nothing is due yet.
    clock.Advance(TimeSpan.FromMinutes(4));
    await Task.Delay(50);
    Check(outbox.Sent.Count == 0 && (await LoadAsync(store, "remind-alice")).Status == CheckpointStatus.Interrupted,
        "expected nothing sent before the timer is due");
    Console.WriteLine("  09:04 on the clock: still waiting, nothing sent");

    clock.Advance(TimeSpan.FromMinutes(2));
    await Until(async () => (await LoadAsync(store, "remind-alice")).Status == CheckpointStatus.Completed,
        "the wake driver to resume remind-alice", () => clock.Advance(wake.Interval));
    var log = await LogAsync(host, "remind-alice");
    Console.WriteLine($"  Past 09:05: the driver woke it. Checkpoint log: {string.Join(" -> ", log.Select(c => c.Status))}");
    Console.WriteLine($"  Sent: {string.Join(", ", outbox.Sent)}");
    Check(outbox.Sent.SequenceEqual(["alice: stand-up at 09:05"]), "expected one reminder sent");
    // Running before Schedule and after it, Interrupted at the timer, then the driver's resume claims
    // the thread (Running) and the run completes.
    Check(
        log.Select(c => c.Status).SequenceEqual([
            CheckpointStatus.Running, CheckpointStatus.Running, CheckpointStatus.Interrupted,
            CheckpointStatus.Running, CheckpointStatus.Completed,
        ]),
        "expected the log Running -> Running -> Interrupted -> Running -> Completed");

    // --- Scenario 3: stopping lets the run in flight finish ---

    Console.WriteLine();
    Console.WriteLine("=== Scenario 3: stopping the host with a woken run in flight ===");
    await desk.ScheduleAsync("remind-carol", "carol", "deploy window");
    await desk.ScheduleAsync("remind-dave", "dave", "deploy window");
    outbox.Hold();
    clock.Advance(TimeSpan.FromMinutes(6));
    await Until(() => Task.FromResult(outbox.Started == 1), "a woken run to reach Send", () => clock.Advance(wake.Interval));
    Console.WriteLine("  Both are due; the driver woke one, and its Send is held open.");

    var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
    logs.Note("-- StopAsync called --");
    var stopping = host.StopAsync();
    await Until(() => Task.FromResult(lifetime.ApplicationStopping.IsCancellationRequested), "the host to begin stopping");

    // The run is parked on the Outbox's gate, so nothing but our Release can let the host finish.
    Console.WriteLine($"  StopAsync called. Host stopped yet? {stopping.IsCompleted} -- it waits for the run.");
    Check(!stopping.IsCompleted, "expected the host to wait for the run in flight");

    outbox.Release();
    await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine("  Released the send. The host stopped:");
    var all = new List<Checkpoint>();
    foreach (var id in (string[])["remind-carol", "remind-dave"])
    {
        var latest = await LoadAsync(store, id);
        all.AddRange(await LogAsync(host, id));
        Console.WriteLine($"    {id}: {latest.Status}{(latest.Status == CheckpointStatus.Interrupted ? " (still waiting on its timer)" : "")}");
    }

    var cancelled = all.Count(c => c.Status == CheckpointStatus.Cancelled);
    Console.WriteLine($"    sends started: {outbox.Started}, cancelled checkpoints: {cancelled}");
    Check(outbox.Started == 1 && outbox.Sent.Count == 2, "expected exactly one woken run, and it finished");
    Check(
        all.Count(c => c.Status == CheckpointStatus.Completed) == 1
            && all.GroupBy(c => c.ThreadId).Count(g => g.Last().Status == CheckpointStatus.Interrupted) == 1,
        "expected one thread finished and the other still waiting");
    Check(cancelled == 0, "expected nothing cancelled");

    Console.WriteLine("  The driver's log on this host:");
    foreach (var line in logs.Take())
    {
        Console.WriteLine($"    {line}");
    }

    host.Dispose();

    // The thread left behind is on disk. A new host on the same file wakes it in its first round.
    Console.WriteLine("  A second host on the same file:");
    host = BuildHost(clock, dbPath, logs, _ => ReminderGraph(withOrphan: false));
    await host.StartAsync();
    store = host.Services.GetRequiredService<ICheckpointStore>();
    outbox = host.Services.GetRequiredService<Outbox>();
    await Until(
        async () => (await LoadAsync(store, "remind-carol")).Status == CheckpointStatus.Completed
            && (await LoadAsync(store, "remind-dave")).Status == CheckpointStatus.Completed,
        "the second host to wake the left-over thread",
        () => clock.Advance(wake.Interval));
    Console.WriteLine($"    woke the one left behind and sent it: {string.Join(", ", outbox.Sent)}");
    Check(outbox.Sent.Count == 1, "expected the second host to send only the one left behind");
    logs.Take();

    // --- Scenario 4: the health checks during an outage ---

    Console.WriteLine();
    Console.WriteLine("=== Scenario 4: the store breaks under the running host ===");

    // Simulate an outage: drop the store's tables behind its back.
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
    {
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE checkpoints; DROP TABLE checkpoint_threads;";
        await drop.ExecuteNonQueryAsync();
    }

    var health = host.Services.GetRequiredService<HealthCheckService>();
    var checkpoints = (await health.CheckHealthAsync()).Entries[StrataHostExtensions.CheckpointsCheck];
    Console.WriteLine($"  Health {StrataHostExtensions.CheckpointsCheck}: {checkpoints.Status} ({checkpoints.Description})");
    Check(checkpoints.Status == HealthStatus.Unhealthy, "expected the checkpoint check to fail");

    var status = host.Services.GetRequiredService<IStrataDriverStatus>();
    await Until(() => Task.FromResult(status.Drivers.Single().FailuresInARow > 0), "a driver round to fail",
        () => clock.Advance(wake.Interval));
    var drivers = (await health.CheckHealthAsync()).Entries[StrataHostExtensions.DriversCheck];
    var driver = status.Drivers.Single();
    Console.WriteLine($"  Health {StrataHostExtensions.DriversCheck}: {drivers.Status} ({driver.Name}'s last round failed: {driver.LastFailure})");
    Console.WriteLine($"  The host is still running: {!host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested}");
    Check(drivers.Status == HealthStatus.Degraded, "expected the driver check to be degraded, not unhealthy");

    // The driver now sits in its back-off wait (twice the interval, on a clock that no longer moves).
    // Stopping ends a wait at once: there is no run to let finish.
    await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    host.Dispose();
    Console.WriteLine("  Stopped with the driver in its back-off wait, on a clock that wasn't moving.");

    // --- Scenario 5: a structural error stops the host from starting ---

    Console.WriteLine();
    Console.WriteLine("=== Scenario 5: a graph with a structural error ===");
    host = BuildHost(clock, dbPath, logs, _ => ReminderGraph(withOrphan: true));
    try
    {
        await host.StartAsync();
        throw new InvalidOperationException("expected the host to refuse to start");
    }
    catch (GraphCompilationException refused)
    {
        foreach (var error in refused.Result.Errors)
        {
            Console.WriteLine($"  StartAsync threw: [{error.Code}] {error.Message}");
        }

        Check(refused.Result.Errors.Any(e => e.Code == "STRATA_ERR_ORPHAN"), "expected an orphan node error");
    }

    var started = host.Services.GetRequiredService<IStrataDriverStatus>().Drivers.Count;
    Console.WriteLine($"  Drivers started: {started}. The graph compiles at start-up, not at its first run.");
    Check(started == 0, "expected no driver to start");
    host.Dispose();

    Console.WriteLine();
    Console.WriteLine("=== Done: one AddStrata() chain gave the store, the runner, a wake loop, health checks and a clean stop. ===");
}
finally
{
    SqliteConnection.ClearAllPools();
    File.Delete(dbPath);
}

static IHost BuildHost(
    TimeProvider clock, string dbPath, DriverLog logs, Func<IServiceProvider, CompiledGraph<Reminder>> graph)
{
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
    builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false);
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Strata:Sqlite:Path"] = dbPath });
    builder.Logging.AddProvider(logs);

    // Registered before AddStrata(), which only adds TimeProvider.System when there is none.
    builder.Services.AddSingleton(clock);
    builder.Services.AddSingleton<Outbox>();
    builder.Services.AddSingleton<ReminderDesk>();
    builder.Services.AddStrata()
        .AddSqliteCheckpoints()
        .AddHealthChecks()
        .AddGraph(graph)
        .AddWakeDriver<Reminder>();
    return builder.Build();
}

// Schedule -> Wait (five minutes) -> Send. Send reaches the Outbox through the container, which the
// runner puts in every run's options (NodeContext.Services).
static CompiledGraph<Reminder> ReminderGraph(bool withOrphan)
{
    var graph = new StateGraph<Reminder>()
        .AddNode("Schedule", reminder => reminder with { Text = reminder.Text.Trim() })
        .AddTimer("Wait", TimeSpan.FromMinutes(5))
        .AddNode("Send", async (Reminder reminder, NodeContext context, CancellationToken _) =>
        {
            await context.Services!.GetRequiredService<Outbox>().SendAsync(reminder);
            return reminder with { Sent = true };
        })
        .AddEdge("Schedule", "Wait")
        .AddEdge("Wait", "Send");

    // A node someone added and forgot to wire in.
    if (withOrphan)
    {
        graph.AddNode("Escalate", reminder => reminder);
    }

    // EveryNode: a checkpoint after each node and a completed one at the end, so the store shows what
    // the driver did. The default (Boundaries) writes nothing when a run completes.
    return graph.Compile(new CompilationOptions { Checkpointing = CheckpointPolicy.EveryNode });
}

static async Task<Checkpoint> LoadAsync(ICheckpointStore store, string threadId) =>
    await store.LoadAsync(threadId) ?? throw new InvalidOperationException($"no checkpoint for {threadId}");

static async Task<List<Checkpoint>> LogAsync(IHost host, string threadId) =>
    [.. (await host.Services.GetRequiredService<ICheckpointQuery>().GetLogAsync(threadId)).Entries.Select(e => e.Checkpoint)];

// Waits for something a background driver does. `nudge` moves the fake clock between looks, so a
// driver sitting in its interval gets to its next round. The 10-second cap only stops a broken demo
// from hanging; nothing asserts how long it took.
static async Task Until(Func<Task<bool>> condition, string what, Action? nudge = null)
{
    var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
    while (!await condition())
    {
        if (DateTime.UtcNow > giveUp)
        {
            throw new InvalidOperationException($"gave up waiting for {what}");
        }

        nudge?.Invoke();
        await Task.Delay(10);
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Reminder(string To, string Text, bool Sent);

/// <summary>Where reminders go. <see cref="Hold"/> makes the next send wait until <see cref="Release"/>.</summary>
sealed class Outbox
{
    private readonly ConcurrentQueue<string> _sent = new();
    private TaskCompletionSource? _gate;
    private int _started;

    public IReadOnlyList<string> Sent => [.. _sent];

    public int Started => Volatile.Read(ref _started);

    public void Hold()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _started, 0);
    }

    public void Release() => _gate?.TrySetResult();

    public async Task SendAsync(Reminder reminder)
    {
        Interlocked.Increment(ref _started);
        if (_gate is { } gate)
        {
            await gate.Task;
        }

        _sent.Enqueue($"{reminder.To}: {reminder.Text}");
    }
}

/// <summary>The application's own service: it gets the runner injected, as any worker or endpoint would.</summary>
sealed class ReminderDesk(StrataRunner<Reminder> runner)
{
    public Task<GraphExecutionResult<Reminder>> ScheduleAsync(string threadId, string to, string text) =>
        runner.RunAsync(threadId, new Reminder(to, text, Sent: false));
}

/// <summary>Keeps the hosting package's driver log lines (category Datalium.Strata.Hosting.*) to print later.</summary>
sealed class DriverLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    /// <summary>A marker of the demo's own, so the log shows where the main flow was.</summary>
    public void Note(string line) => _lines.Enqueue(line);

    public List<string> Take()
    {
        var lines = new List<string>();
        while (_lines.TryDequeue(out var line))
        {
            lines.Add(line);
        }

        return lines;
    }

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information && category.StartsWith("Datalium.Strata.Hosting.", StringComparison.Ordinal);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lines.Enqueue($"[{logLevel}] {formatter(state, exception)}");
            }
        }
    }
}
