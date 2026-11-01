using SpawnDev.AI.Server;

namespace Vertex.AI;

/// <summary>
/// Gets Vertex's language model onto a PC that has never seen it, without Ollama: the dashboard's "Install" button and `vertex install-models` both come here. The work is
/// SpawnDev.AI.Server's <see cref="OllamaModelInstaller"/> (models.spawndev.com first, registry.ollama.ai second, resumable, every file checked against its SHA-256, the manifest
/// written last so a half-done model is invisible); this class only decides WHAT to install and how to say how it is going. The model lands in the Ollama-layout cache that
/// <see cref="VertexAi"/> reads, so it is the same place Ollama itself would put it.
/// </summary>
public static class ModelSetup
{
    /// <summary>
    /// The model Vertex installs when nothing else is asked for. llama3.1:8b is the default because the tests are calibrated on it and it won the earlier comparisons;
    /// it is a default, not a requirement: any model <see cref="ListInstallableAsync"/> returns (or that Ollama already put in the cache) can be chosen instead.
    /// </summary>
    public const string DefaultModel = "llama3.1:8b";

    /// <summary>One HttpClient for the installer: its own timeout is infinite (a 5 GB file outlasts any fixed one) and the installer guards each read with a stall timeout.</summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Whether <paramref name="name"/> is complete in the cache (manifest and blobs both there; a half-installed model does not list).</summary>
    public static bool IsInstalled(string name, string? modelsRoot = null) => new OllamaModelStore(modelsRoot).Resolve(name) != null;

    /// <summary>The names of the models in the cache.</summary>
    public static IReadOnlyList<string> Installed(string? modelsRoot = null) => new OllamaModelStore(modelsRoot).List().Select(m => m.Name).ToList();

    /// <summary>Installs <paramref name="name"/> unless it is already complete. Progress is reported a few times a second; cancelling keeps the partial file, so the next call resumes it.</summary>
    public static Task<OllamaModel> InstallAsync(string name, Action<OllamaInstallProgress>? progress = null, CancellationToken ct = default,
        string? modelsRoot = null, IReadOnlyList<OllamaModelSource>? sources = null)
        => new OllamaModelInstaller(Http, modelsRoot, sources) { Progress = progress }.InstallAsync(name, ct);

    /// <summary>The models a source hosts that can be installed (models.spawndev.com has an index; a source that cannot be reached is skipped).</summary>
    public static Task<IReadOnlyList<OllamaInstallableModel>> ListInstallableAsync(CancellationToken ct = default, string? modelsRoot = null, IReadOnlyList<OllamaModelSource>? sources = null)
        => new OllamaModelInstaller(Http, modelsRoot, sources).ListInstallableAsync(ct);

    /// <summary>One line for a progress report: "Downloading llama3.1:8b: 1.2 of 4.9 GB (35 MB/s) from models.spawndev.com".</summary>
    public static string Describe(OllamaInstallProgress p)
    {
        switch (p.Stage)
        {
            case "manifest": return $"Looking up {p.Model}...";
            case "verify": return $"Checking {p.Model}...";
            case "done": return $"{p.Model} is installed.";
        }
        var speed = p.BytesPerSecond > 0 ? $" ({Size(p.BytesPerSecond)}/s)" : "";
        var from = string.IsNullOrEmpty(p.Source) ? "" : $" from {p.Source}";
        return $"Downloading {p.Model}: {Size(p.DoneBytes)} of {Size(p.TotalBytes)}{speed}{from}";
    }

    /// <summary>How far an install is, 0 to 1 (0 when the total is not known yet).</summary>
    public static double Fraction(OllamaInstallProgress p) => p.TotalBytes > 0 ? Math.Clamp((double)p.DoneBytes / p.TotalBytes, 0, 1) : 0;

    /// <summary>"4.9 GB", "35 MB", "812 KB": decimal units, the way a download page shows them.</summary>
    public static string Size(double bytes) => bytes switch
    {
        >= 1e9 => $"{bytes / 1e9:0.0} GB",
        >= 1e6 => $"{bytes / 1e6:0} MB",
        >= 1e3 => $"{bytes / 1e3:0} KB",
        _ => $"{bytes:0} B",
    };
}
