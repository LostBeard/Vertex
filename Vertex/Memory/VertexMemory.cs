using ILGPU.Runtime;
using SpawnDev.AI;
using SpawnDev.AI.Server;
using SpawnDev.ILGPU.ML.Hub;
using Vertex.AI;

namespace Vertex.Memory;

/// <summary>One memory as the control panel shows it: when, what kind, what it says, which server, and whether it is about a particular person.</summary>
public sealed record MemoryRow(string Id, DateTime WhenLocal, string Kind, string Text, string Scope, bool AboutAPerson);

/// <summary>
/// Vertex's long-term memory: Data's <see cref="AiMemoryStore"/> (what is remembered, with the privacy rules) and <see cref="AiMemoryEngine"/> (embedding and search on the GPU), opened on files in
/// Vertex's own folder and run through the shared GPU lane like every other model call. Everything stays on this PC.
/// What goes IN is decided by Vertex (<see cref="Record"/> is called by the brain for jobs that ended, things asked to be remembered...), never raw chat or ticks; what comes OUT is a few short
/// lines for the prompt (<see cref="RecallAsync"/>). A recall is urgent lane work, a record is background work.
/// </summary>
public sealed class VertexMemory : IDisposable
{
    /// <summary>The embedding model: the one the fast command matcher already uses (all-MiniLM-L6-v2, 384 numbers).</summary>
    public const string ModelRepo = ModelHub.KnownModels.AllMiniLmL6V2;
    public const int Dimension = 384;

    private readonly AiMemoryEngine _engine;
    private readonly GpuLane? _lane;
    private readonly string _metaFile, _vectorFile;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private VertexMemory(AiMemoryEngine engine, GpuLane? lane, string folder)
    {
        _engine = engine;
        _lane = lane;
        _metaFile = Path.Combine(folder, "memory.json");
        _vectorFile = Path.Combine(folder, "memory.vectors");
    }

    /// <summary>The memories themselves: listing, deleting, forgetting a person, pausing learning (the privacy page works on this).</summary>
    public AiMemoryStore Store => _engine.Store;

    /// <summary>Opens the memory kept in <paramref name="folder"/> (made if there is none) and loads the embedding model.</summary>
    public static async Task<VertexMemory> OpenAsync(IModelSource models, Accelerator accelerator, string folder, GpuLane? lane = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var meta = Path.Combine(folder, "memory.json");
        var vectors = Path.Combine(folder, "memory.vectors");
        AiMemoryStore store;
        if (File.Exists(meta) && File.Exists(vectors))
        {
            await using var m = File.OpenRead(meta);
            await using var v = File.OpenRead(vectors);
            store = await AiMemoryStore.LoadAsync(m, v, ct).ConfigureAwait(false);
        }
        else store = new AiMemoryStore(ModelRepo, Dimension);
        var engine = new AiMemoryEngine(models, accelerator, store) { ModelRepo = ModelRepo, Dimension = Dimension };
        var memory = new VertexMemory(engine, lane, folder);
        using (lane == null ? null : await lane.EnterAsync(urgent: false).ConfigureAwait(false))
            await engine.EnsureReadyAsync(ct).ConfigureAwait(false);
        return memory;
    }

    /// <summary>
    /// Remembers one thing. Returns its id, or null when nothing was stored (learning is paused, or it is about someone nothing may be learned about). Saved to disk before returning.
    /// </summary>
    public async Task<string?> RecordAsync(AiMemoryEntry entry, CancellationToken ct = default)
    {
        string? id;
        using (_lane == null ? null : await _lane.EnterAsync(urgent: false).ConfigureAwait(false))
            id = await _engine.RememberAsync(entry, ct).ConfigureAwait(false);
        if (id != null) await SaveAsync(ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// The best <paramref name="k"/> memories for what was just said, in this <paramref name="scope"/> (a server's "host:port") or in none, newest and most important ranked up.
    /// Urgent lane work, unless <paramref name="laneHeld"/>: a tool the language model calls runs INSIDE the chat turn, which holds the GPU lane for the whole reply, and asking for the lane
    /// again from there waits on its own caller forever (found 2026-11-02 by LongTermMemoryBrainTests: the run sat 54 minutes with "RecallAsync (urgent) has waited 3142 s; held by ChatAsync").
    /// </summary>
    public async Task<IReadOnlyList<AiMemoryHit>> RecallAsync(string query, string scope, int k = 3, IReadOnlyCollection<AiMemoryKind>? kinds = null, CancellationToken ct = default, bool laneHeld = false)
    {
        using (_lane == null || laneHeld ? null : await _lane.EnterAsync(urgent: true).ConfigureAwait(false))
            return await _engine.SearchAsync(query, k, new AiMemoryFilter { Scope = scope, Kinds = kinds }, null, null, ct).ConfigureAwait(false);
    }

    /// <summary>Every memory, newest first, as the control panel lists them. Nothing here leaves this PC, and nothing is looked up by a person's name: a subject is an id.</summary>
    public IReadOnlyList<MemoryRow> Rows() => Store.List(new AiMemoryFilter { IncludeSuperseded = true })
        .OrderByDescending(e => e.TimeTicks)
        .Select(e => new MemoryRow(e.Id, new DateTime(e.TimeTicks, DateTimeKind.Utc).ToLocalTime(), e.Kind.ToString().ToLowerInvariant(), e.Text, e.Scope, e.Subjects.Count > 0))
        .ToList();

    /// <summary>
    /// Forgets these memories for good, and whatever was digested from them: gone from the store, from searches and, once the files are rewritten (before this returns), from the disk.
    /// Returns how many were removed.
    /// </summary>
    public async Task<int> ForgetAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var removed = Store.Delete(ids.ToArray());
        await SaveAsync(ct).ConfigureAwait(false);
        return removed.Count;
    }

    /// <summary>Forgets everything he remembers.</summary>
    public Task<int> ForgetAllAsync(CancellationToken ct = default) => ForgetAsync(Rows().Select(r => r.Id), ct);

    /// <summary>Writes the memories to disk (whole file, replaced in one step so a crash leaves the old one).</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var metaTmp = _metaFile + ".tmp";
            var vectorTmp = _vectorFile + ".tmp";
            await using (var m = File.Create(metaTmp))
            await using (var v = File.Create(vectorTmp))
                await Store.SaveAsync(m, v, ct).ConfigureAwait(false);
            File.Move(vectorTmp, _vectorFile, overwrite: true);
            File.Move(metaTmp, _metaFile, overwrite: true);
        }
        finally { _saveGate.Release(); }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _saveGate.Dispose();
    }
}
