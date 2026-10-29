using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Datalium.Strata.Observability;

namespace Datalium.Strata.Demos.RunObservability;

/// <summary>
/// Collects every finished span from the engine's <see cref="ActivitySource"/> (and the handlers' own
/// source) with a plain <see cref="ActivityListener"/>: what an OpenTelemetry exporter would receive
/// after <c>AddSource(StrataTelemetry.Name)</c>, with no OpenTelemetry package involved.
/// </summary>
public sealed class SpanCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private const string StartOrder = "demo.start_order";
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private long _started;

    public SpanCollector(params string[] extraSources)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == StrataTelemetry.Name || extraSources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // Numbered as they start, so a tree can list siblings in start order without relying on
            // clock resolution.
            ActivityStarted = activity => activity.SetCustomProperty(StartOrder, Interlocked.Increment(ref _started)),
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The spans of the trace whose root is the <c>strata.run</c> span with this thread id and display name.</summary>
    public List<Activity> Trace(string threadId, string rootDisplayName)
    {
        var root = _stopped.Single(a =>
            a.OperationName == "strata.run"
            && a.DisplayName == rootDisplayName
            && (string?)a.GetTagItem("strata.thread_id") == threadId);
        return [.. _stopped.Where(a => a.TraceId == root.TraceId)];
    }

    /// <summary>The order <paramref name="span"/> started in, among the spans this collector saw.</summary>
    public static long StartedAt(Activity span) => (long)span.GetCustomProperty(StartOrder)!;

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// Collects every measurement the engine's <see cref="Meter"/> records, with a plain
/// <see cref="MeterListener"/>: what <c>AddMeter(StrataTelemetry.Name)</c> would export.
/// </summary>
public sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _all = new();

    public MetricCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == StrataTelemetry.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public string Tag(string key) => Tags.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    }

    public IReadOnlyList<Measurement> Of(string instrument) => [.. _all.Where(m => m.Instrument == instrument)];

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value;
        }

        _all.Enqueue(new Measurement(instrument.Name, value, map));
    }
}

/// <summary>An observer with a bug: it throws on the first event it is given, then behaves.</summary>
public sealed class ThrowsOnFirstEvent : IRunObserver
{
    private int _calls;

    public ConcurrentQueue<RunEvent> Seen { get; } = new();

    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnEvent(RunEvent runEvent)
    {
        if (Interlocked.Increment(ref _calls) == 1)
        {
            throw new InvalidOperationException("observer bug");
        }

        Seen.Enqueue(runEvent);
        if (runEvent is RunCompleted)
        {
            Done.TrySetResult();
        }
    }
}

/// <summary>A stuck observer: every event waits for <paramref name="gate"/>, like a sink whose network is down.</summary>
public sealed class StuckObserver(ManualResetEventSlim gate) : IRunObserver
{
    public ConcurrentQueue<RunEvent> Seen { get; } = new();

    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnEvent(RunEvent runEvent)
    {
        gate.Wait();
        Seen.Enqueue(runEvent);
        if (runEvent is RunCompleted)
        {
            Done.TrySetResult();
        }
    }
}
