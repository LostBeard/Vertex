using System.Text.Json;

namespace Vertex.Host;

/// <summary>
/// What he did, kept on disk, because the panel holds only the last 500 lines in memory: <c>play-DATE.log</c> (what the panel shows, without what people say) and <c>status-DATE.jsonl</c> (a snapshot every so often, in the game or not).
/// Bounded: past <see cref="MaxBytes"/> the oldest days go, and with under <see cref="MinFreeBytes"/> free on the drive nothing is written. Speech and chat are never written: said to him or not, this file has no use for it.
/// A full or locked disk costs a line, never the game.
/// </summary>
public sealed class PlayLog
{
    private readonly string _folder;
    private readonly object _lock = new();
    private int _writes;
    private bool _noRoom;

    public long MaxBytes { get; init; } = 500L * 1024 * 1024;
    public long MinFreeBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    /// <summary>Free space on the drive. Tests replace it.</summary>
    public Func<long> FreeBytes { get; init; }

    public PlayLog(string folder)
    {
        _folder = folder;
        FreeBytes = () => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
    }

    /// <summary>Game events reach him as speech, marked; those are facts about the game, and are kept.</summary>
    public static bool Keeps(LineKind kind, string text) => kind switch
    {
        LineKind.System or LineKind.Tool or LineKind.Error => true,
        LineKind.Heard => text.StartsWith("[Game update", StringComparison.Ordinal),
        _ => false,
    };

    public void Line(DateTime time, LineKind kind, string text)
    {
        if (Keeps(kind, text)) Append($"play-{time:yyyy-MM-dd}.log", $"{time:HH:mm:ss} {kind}: {text}\n");
    }

    /// <summary>One line of how he is, from anything that serializes; null when he is not in a game.</summary>
    public void Status(DateTime time, object? state)
    {
        try { Append($"status-{time:yyyy-MM-dd}.jsonl", JsonSerializer.Serialize(new { time, inGame = state != null, state }) + "\n"); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or JsonException) { }
    }

    private void Append(string file, string text)
    {
        lock (_lock)
        {
            try
            {
                // Not on every line: a look at the disk every 200, and at the first.
                if (_writes++ % 200 == 0)
                {
                    _noRoom = FreeBytes() < MinFreeBytes;
                    if (!_noRoom) Prune();
                }
                if (_noRoom) return;
                Directory.CreateDirectory(_folder);
                File.AppendAllText(Path.Combine(_folder, file), text);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Prune()
    {
        if (!Directory.Exists(_folder)) return;
        var files = new DirectoryInfo(_folder).GetFiles().OrderBy(f => f.Name[(f.Name.IndexOf('-') + 1)..]).ThenBy(f => f.Name).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= MaxBytes) break;
            total -= f.Length;
            f.Delete();
        }
    }
}
