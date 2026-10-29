// Long-term memory: remember by meaning, recall by similarity (design/45-Long-Term-Memory.md).
//
// StrataMemory joins a memory store (IMemoryStore) and an embedding generator
// (IEmbeddingGenerator<string, Embedding<float>>). RememberAsync embeds a text and stores it with
// the vector; RecallAsync embeds a text and returns the most similar records by cosine similarity.
// The store never calls a model: it takes vectors. Here the store is the engine's
// InMemoryMemoryStore, and the embedder is BagOfWords, a deterministic stand-in written below:
// each word is hashed into one of 256 buckets, so texts that share words really are closer.
//
// The demo is an incident desk. Past incidents are remembered for two tenants. A graph -- Recall ->
// Diagnose -> Remember -- recalls the incidents most like a new one, reuses the closest one's fix,
// and remembers the new incident, so the next run can recall it. The tenant comes from the run's
// metadata ("tenant"), never from the node's code. Then the demo shows the search's filters, that
// vectors are labelled by model, that an upsert keeps a record's creation time, and purging by age.
//
// No live model and no API key. A FakeTimeProvider is the store's clock, so a year passes in a line.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Datalium.Strata.AI;
using Datalium.Strata.Graph;
using Datalium.Strata.Interruption;
using Datalium.Strata.Memory;
using Datalium.Strata.Observability;
using Datalium.Strata.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;

var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
var clock = new FakeTimeProvider(start);
var store = new InMemoryMemoryStore(BagOfWords.Dimensions, clock);
var embedder = new BagOfWords("demo", "bow-256");
var memory = new StrataMemory(store, embedder);

// --- Step 1: remember past incidents, for two tenants. ---

Console.WriteLine("=== Step 1: remember past incidents (one embedding call for six entries) ===");
await memory.RememberManyAsync(
[
    Past("acme", "inc-101", "checkout", "Checkout pods crash looping, out of memory killed after deploy", "a memory leak in the v2.3 image", "roll back the deploy"),
    Past("acme", "inc-102", "checkout", "Checkout latency spike, database connection pool exhausted", "a connection pool too small for the sale", "raise the pool size"),
    Past("acme", "inc-103", "search", "Search pods out of memory killed during the nightly reindex", "a reindex batch too large", "halve the reindex batch"),
    Past("acme", "inc-104", "payments", "Payments API returns 502 bad gateway, upstream certificate expired", "an expired upstream certificate", "renew the certificate"),
    Past("acme", "inc-105", "checkout", "Checkout error rate high after a new feature flag was enabled", "a bug behind the flag", "turn the flag off"),
    Past("acme", "inc-106", "auth", "Login failures, TLS handshake errors after certificate rotation", "a missing intermediate certificate", "redeploy the full chain"),
]);
Console.WriteLine($"  acme: 6 incidents remembered in {embedder.Calls} embedding call(s), labelled '{memory.Model}'");
Check(embedder.Calls == 1, "expected RememberManyAsync to embed every entry in one call");
Check(memory.Model == "demo/bow-256", "expected the label <provider>/<model> from the generator's metadata");

// Another tenant has an incident with the same id and the very same symptoms, and another cause.
await memory.RememberAsync(Past("globex", "inc-101", "checkout", "Checkout pods crash looping, out of memory killed after deploy", "a noisy neighbour on the node", "move the pods to another node pool"));
// Another namespace: a runbook, not an incident.
await memory.RememberAsync(new MemoryEntry("runbooks", "rb-oom", "Pods out of memory killed: raise the memory limit or roll back the deploy") { Tenant = "acme" });
Console.WriteLine("  globex: 1 incident (same id inc-101, same symptoms, another cause); acme: 1 runbook in namespace 'runbooks'");

// --- Step 2: the incident desk graph. ---

Console.WriteLine();
Console.WriteLine("=== Step 2: an incident desk that recalls similar incidents (Recall -> Diagnose -> Remember) ===");
var desk = new StateGraph<Incident>()
    .AddNode("Recall", async (incident, ctx, ct) =>
    {
        // No tenant here: StrataMemory reads it from the run's metadata "tenant".
        var matches = await memory.RecallAsync("incidents", incident.Symptoms, new RecallOptions
        {
            Limit = 3,
            MinSimilarity = 0.5,
            Exclude = [incident.Id],
        }, ctx, ct);
        return incident with { Similar = [.. matches.Select(Recalled.From)] };
    })
    .AddNode("Diagnose", incident => incident.Similar is [var closest, ..]
        ? incident with { Diagnosis = $"likely {closest.RootCause} (like {closest.Id})", Action = closest.Action }
        : incident with { Diagnosis = "no similar incident", Action = "page the on-call engineer" })
    .AddNode("Remember", async (incident, ctx, ct) =>
    {
        await memory.RememberAsync(new MemoryEntry("incidents", incident.Id, incident.Symptoms)
        {
            Content = new JsonObject { ["rootCause"] = incident.Diagnosis, ["action"] = incident.Action },
            Metadata = new Dictionary<string, string> { ["service"] = incident.Service },
        }, ctx, ct);
        return incident;
    })
    .AddEdge("Recall", "Diagnose")
    .AddEdge("Diagnose", "Remember")
    .Compile();

var first = await Handle("acme", new Incident("inc-201", "checkout", "Checkout pods crash looping again, out of memory after this morning's deploy"));
Check(first.Similar.Count > 0 && first.Similar[0].Id == "inc-101", "expected acme's inc-101 to be the closest");
Check(first.Action == "roll back the deploy", "expected inc-101's fix to be reused");
Check(first.Similar.All(m => m.Id.StartsWith("inc-", StringComparison.Ordinal)), "expected only incidents, never the runbook");

var second = await Handle("acme", new Incident("inc-202", "checkout", "Checkout out of memory, pods crash looping after the deploy"));
Check(second.Similar.Any(m => m.Id == "inc-201"), "expected the incident remembered by the last run to be recalled");

var globex = await Handle("globex", new Incident("gx-7", "checkout", "Checkout pods crash looping again, out of memory after this morning's deploy"));
Check(globex.Similar.Select(m => m.Id).SequenceEqual(["inc-101"]), "expected globex to recall only its own inc-101");
Check(globex.Action == "move the pods to another node pool", "expected globex's own fix, not acme's");

var unrelated = await Handle("acme", new Incident("inc-203", "billing", "Billing emails delayed, the mail queue backlog keeps growing"));
Check(unrelated.Similar.Count == 0 && unrelated.Action == "page the on-call engineer", "expected nothing above 0.5 and an escalation");

// Without a tenant, a recall reads only tenant-less records: here, none.
var noTenant = await memory.RecallAsync("incidents", "Checkout pods crash looping, out of memory killed after deploy");
Console.WriteLine($"  The same recall with no tenant (no run metadata): {noTenant.Count} match(es)");
Check(noTenant.Count == 0, "expected a recall without a tenant to read no tenant's records");

// --- Step 3: the search's filters. ---

Console.WriteLine();
Console.WriteLine("=== Step 3: filters -- metadata, minimum similarity, limit, excluded ids, namespace ===");
const string Query = "pods out of memory killed";
var all = await Recall(Query, new RecallOptions { Tenant = "acme" }, "everything (limit 10)");
var bySearch = await Recall(Query, new RecallOptions { Tenant = "acme", Metadata = new Dictionary<string, string> { ["service"] = "search" } }, "metadata service=search");
var close = await Recall(Query, new RecallOptions { Tenant = "acme", MinSimilarity = 0.5 }, "MinSimilarity 0.5");
var top3 = await Recall(Query, new RecallOptions { Tenant = "acme", Limit = 3 }, "Limit 3");
var excluded = await Recall(Query, new RecallOptions { Tenant = "acme", Exclude = [all[0].Record.Id] }, $"Exclude [{all[0].Record.Id}]");
var runbooks = await memory.RecallAsync("runbooks", Query, new RecallOptions { Tenant = "acme" });
Console.WriteLine($"  namespace 'runbooks': {string.Join(", ", runbooks.Select(Line))}");

Check(all.Count == 9, "expected acme's nine incidents (six past, three from the desk)");
Check(IsRanked(all), "expected the matches most similar first");
// inc-101 and inc-103 share exactly the same query words, so they tie, and a tie is ordered by id.
Check(all[0].Similarity == all[1].Similarity && Ids(all).Take(2).SequenceEqual(["inc-101", "inc-103"]), "expected a tie ordered by id");
Check(bySearch.Select(m => m.Record.Id).SequenceEqual(["inc-103"]), "expected only the search service's incident");
Check(close.Count > 0 && close.Count < all.Count && close.All(m => m.Similarity >= 0.5), "expected MinSimilarity to drop the weaker matches");
Check(Ids(top3).SequenceEqual(Ids(all).Take(3)), "expected Limit 3 to keep the three best");
Check(Ids(excluded).SequenceEqual(Ids(all).Skip(1)), "expected Exclude to drop that id and nothing else");
Check(runbooks.Select(m => m.Record.Id).SequenceEqual(["rb-oom"]) && all.All(m => m.Record.Id != "rb-oom"), "expected namespaces never to cross");

// --- Step 4: vectors are labelled by model. ---

Console.WriteLine();
Console.WriteLine("=== Step 4: another model's memory reads none of these records ===");
// The same words, the same buckets, the same vectors -- but another model name.
var upgraded = new StrataMemory(store, new BagOfWords("demo", "bow-256-v2"));
var v2 = await upgraded.RecallAsync("incidents", Query, new RecallOptions { Tenant = "acme" });
Console.WriteLine($"  '{upgraded.Model}' recall: {v2.Count} match(es); '{memory.Model}' recall: {all.Count}");
Check(v2.Count == 0, "expected a search with another model's label to find nothing, even with identical vectors");

// --- Step 5: an upsert replaces a record but keeps its creation time. ---

Console.WriteLine();
Console.WriteLine("=== Step 5: remembering inc-101 again 300 days later ===");
clock.Advance(TimeSpan.FromDays(300));
await memory.RememberAsync(Past("acme", "inc-101", "checkout", "Checkout pods crash looping, out of memory killed after deploy", "a memory leak in the v2.3 image (postmortem PM-17)", "roll back the deploy"));
var updated = await store.GetAsync(new MemoryKey("incidents", "acme", "inc-101"));
Console.WriteLine($"  created {updated!.CreatedAt:yyyy-MM-dd}, updated {updated.UpdatedAt:yyyy-MM-dd}, root cause: {updated.Content!["rootCause"]}");
Check(updated.CreatedAt == start && updated.UpdatedAt == start.AddDays(300), "expected CreatedAt kept and UpdatedAt moved");
Check(updated.Content!["rootCause"]!.GetValue<string>().Contains("PM-17", StringComparison.Ordinal), "expected the content replaced");

// --- Step 6: purge by age. ---

Console.WriteLine();
Console.WriteLine("=== Step 6: 400 days in, purge incidents not written for 365 days ===");
clock.Advance(TimeSpan.FromDays(100));
var cutoff = clock.GetUtcNow().AddDays(-365);
var purged = await store.PurgeAsync(new MemoryPurge(cutoff) { Namespace = "incidents" });
var acmeLeft = (await store.ListAsync(new MemoryQuery("incidents") { Tenant = "acme" })).Records;
var globexLeft = (await store.ListAsync(new MemoryQuery("incidents") { Tenant = "globex" })).Records;
var runbooksLeft = (await store.ListAsync(new MemoryQuery("runbooks") { Tenant = "acme" })).Records;
Console.WriteLine($"  Purged {purged} record(s) last written before {cutoff:yyyy-MM-dd}.");
Console.WriteLine($"  Left: acme incidents [{string.Join(", ", acmeLeft.Select(r => r.Id))}], globex incidents [{string.Join(", ", globexLeft.Select(r => r.Id))}], acme runbooks [{string.Join(", ", runbooksLeft.Select(r => r.Id))}]");
Check(purged == 10, "expected ten records purged: acme's 102-106 and 201-203, globex's inc-101 and gx-7");
Check(acmeLeft.Select(r => r.Id).SequenceEqual(["inc-101"]), "expected the record updated at day 300 to survive");
Check(globexLeft.Count == 0, "expected the purge to cover every tenant when it names none");
Check(runbooksLeft.Select(r => r.Id).SequenceEqual(["rb-oom"]), "expected the purge to leave another namespace alone");

Console.WriteLine();
Console.WriteLine("=== Done: memories were recalled by meaning, kept apart by tenant, namespace and model, updated in place and purged by age. ===");

async Task<Incident> Handle(string tenant, Incident incident)
{
    var channel = new RunEventChannel();
    var result = await desk.RunAsync(incident.Id, incident, new InMemoryCheckpointStore(), new RunOptions
    {
        Observer = channel,
        Metadata = new() { ["tenant"] = tenant },
    });
    var handled = result is GraphExecutionResult<Incident>.Completed completed
        ? completed.State
        : throw new InvalidOperationException($"expected the run to complete, got: {result}");
    var events = new List<RunEvent>();
    await foreach (var e in channel.Reader.ReadAllAsync().WithCancellation(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token))
    {
        events.Add(e);
    }

    Console.WriteLine($"  [{tenant}] {handled.Id}: \"{handled.Symptoms}\"");
    foreach (var match in handled.Similar)
    {
        Console.WriteLine($"      recalled {match.Id} ({match.Similarity:0.000}): {match.RootCause}");
    }

    Console.WriteLine($"      -> {handled.Diagnosis}; action: {handled.Action}");
    var embeddings = events.OfType<ModelCallStarted>().Count(e => e.Operation == StrataEmbeddings.Operation);
    Check(embeddings == 2, "expected two observed embedding calls per run: the recall and the remember");
    return handled;
}

async Task<IReadOnlyList<MemoryMatch>> Recall(string text, RecallOptions options, string label)
{
    var matches = await memory.RecallAsync("incidents", text, options);
    Console.WriteLine($"  {label,-26} {string.Join(", ", matches.Select(Line))}");
    return matches;
}

static string Line(MemoryMatch match) => $"{match.Record.Id} {match.Similarity:0.000}";

static IEnumerable<string> Ids(IEnumerable<MemoryMatch> matches) => matches.Select(m => m.Record.Id);

static bool IsRanked(IReadOnlyList<MemoryMatch> matches) =>
    matches.Zip(matches.Skip(1)).All(pair => pair.First.Similarity >= pair.Second.Similarity);

static MemoryEntry Past(string tenant, string id, string service, string symptoms, string rootCause, string action) =>
    new("incidents", id, symptoms)
    {
        Tenant = tenant,
        Content = new JsonObject { ["rootCause"] = rootCause, ["action"] = action },
        Metadata = new Dictionary<string, string> { ["service"] = service },
    };

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed record Incident(string Id, string Service, string Symptoms)
{
    public IReadOnlyList<Recalled> Similar { get; init; } = [];

    public string Diagnosis { get; init; } = "";

    public string Action { get; init; } = "";
}

sealed record Recalled(string Id, double Similarity, string RootCause, string Action)
{
    public static Recalled From(MemoryMatch match) => new(
        match.Record.Id,
        match.Similarity,
        match.Record.Content?["rootCause"]?.GetValue<string>() ?? "",
        match.Record.Content?["action"]?.GetValue<string>() ?? "");
}

/// <summary>
/// A deterministic stand-in for an embedding model: a hashed bag of words. Each word (lower-cased,
/// stop words dropped, a crude suffix strip) adds 1 to one of 256 buckets chosen by FNV-1a, which,
/// unlike string.GetHashCode, is the same in every process. Texts that share words point the same
/// way, so cosine similarity ranks them as a real model would, if far more crudely.
/// </summary>
sealed partial class BagOfWords(string provider, string model) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const int Dimensions = 256;

    private static readonly HashSet<string> StopWords =
        ["the", "and", "after", "again", "this", "that", "was", "were", "with", "during", "keeps", "new", "from", "for", "out"];

    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var tokens = 0;
        var embeddings = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            var vector = new float[Dimensions];
            foreach (var word in Words(text))
            {
                vector[Bucket(word)] += 1;
                tokens++;
            }

            // An all-zero vector has no direction, and the store refuses it.
            if (vector.All(x => x == 0))
            {
                vector[0] = 1;
            }

            embeddings.Add(new Embedding<float>(vector) { ModelId = model });
        }

        embeddings.Usage = new UsageDetails { InputTokenCount = tokens };
        return Task.FromResult(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata(provider, null, model, Dimensions)
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private static IEnumerable<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant())
            .Select(m => Stem(m.Value))
            .Where(w => w.Length > 2 && !StopWords.Contains(w));

    private static string Stem(string word) =>
        word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal) ? word[..^3]
        : word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal) ? word[..^2]
        : word.Length > 3 && word.EndsWith('s') ? word[..^1]
        : word;

    private static int Bucket(string word)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(word))
        {
            hash = (hash ^ b) * 16777619u;
        }

        return (int)(hash % Dimensions);
    }

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}
