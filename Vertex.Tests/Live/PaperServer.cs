using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vertex.Tests.Live;

/// <summary>
/// A REAL Paper 1.21.5 server (build 114, the build the AubsCraft servers run, SHA-256 checked) started as a local
/// Java process, offline mode, bound to 127.0.0.1. Downloads (Paper jar, Paper's libraries, the JDK) are shared
/// with the AubsCraft tests under %LOCALAPPDATA%\AubsCraft.Tests; each Vertex server keeps its own folder there
/// (servers\vertex-{name}) and starts from a clean world every time.
/// </summary>
public sealed class PaperServer : IAsyncDisposable
{
    public const string GameVersion = "1.21.5";
    private const int Build = 114;
    private const string JarSha256 = "2ae6ae22adf417699746e0f89fc2ef6cb6ee050a5f6608cee58f0535d60b509e";
    public const string Seed = "vertex-tests";

    public static string CacheRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AubsCraft.Tests");

    public string Name { get; }
    public string Dir { get; }
    public int Port { get; }

    private readonly Process _process;
    private readonly List<string> _output = [];
    private readonly List<(Func<string, bool> Match, TaskCompletionSource<string> Done)> _waiters = [];
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PaperServer(string name, string dir, int port, Process process)
    {
        Name = name;
        Dir = dir;
        Port = port;
        _process = process;
    }

    /// <summary>Starts a fresh server (world, lists and config wiped; Paper's downloaded libraries kept).</summary>
    public static async Task<PaperServer> StartAsync(string name, int port, TimeSpan timeout,
        IDictionary<string, string>? properties = null)
    {
        var jar = await EnsureJarAsync();
        var dir = Path.Combine(CacheRoot, "servers", "vertex-" + name);
        Directory.CreateDirectory(dir);
        foreach (var d in Directory.GetDirectories(dir))
        {
            var n = Path.GetFileName(d);
            if (n is "libraries" or "cache" or "versions") continue;
            Directory.Delete(d, recursive: true);
        }
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);

        File.WriteAllText(Path.Combine(dir, "eula.txt"), "eula=true\n");
        var props = new Dictionary<string, string>
        {
            ["server-ip"] = "127.0.0.1",
            ["server-port"] = port.ToString(),
            ["online-mode"] = "false",
            ["max-players"] = "10",
            ["level-seed"] = Seed,
            ["spawn-protection"] = "0",
            ["view-distance"] = "4",
            ["simulation-distance"] = "4",
            ["enable-query"] = "false",
            ["enable-rcon"] = "false",
        };
        if (properties != null) foreach (var (k, v) in properties) props[k] = v;
        File.WriteAllText(Path.Combine(dir, "server.properties"), string.Join('\n', props.Select(p => $"{p.Key}={p.Value}")) + "\n");

        var psi = new ProcessStartInfo(await Jdk.JavaAsync(), ["-Xms512M", "-Xmx1G", "-jar", jar, "--nogui"])
        {
            WorkingDirectory = dir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("java did not start");
        KillOnExitJob.Add(process); // dies with the test host, even if teardown never runs
        var server = new PaperServer(name, dir, port, process);
        process.OutputDataReceived += (_, e) => server.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => server.OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var finished = await Task.WhenAny(server._started.Task, process.WaitForExitAsync(), Task.Delay(timeout));
        if (finished != server._started.Task)
        {
            var tail = string.Join('\n', server.OutputTail(40));
            await server.DisposeAsync();
            throw new InvalidOperationException($"Paper '{name}' did not finish starting (exited={process.HasExited}). Last output:\n{tail}");
        }
        return server;
    }

    private static readonly System.Text.RegularExpressions.Regex Ansi = new(@"\x1b\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void OnLine(string? line)
    {
        if (line == null) return;
        line = Ansi.Replace(line, ""); // Paper colors console output (numbers in /data get, join messages)
        List<TaskCompletionSource<string>>? hits = null;
        lock (_output)
        {
            _output.Add(line);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (!_waiters[i].Match(line)) continue;
                (hits ??= []).Add(_waiters[i].Done);
                _waiters.RemoveAt(i);
            }
        }
        if (hits != null) foreach (var h in hits) h.TrySetResult(line);
        // "[12:00:00 INFO]: Done (12.345s)! For help, type "help""
        if (line.Contains("Done (") && line.Contains("For help")) _started.TrySetResult();
    }

    /// <summary>Waits for a console line (from now on, or already printed when includeHistory) matching the predicate.</summary>
    public async Task<string> WaitForLineAsync(Func<string, bool> match, TimeSpan timeout, bool includeHistory = false)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_output)
        {
            if (includeHistory)
                foreach (var line in _output)
                    if (match(line)) return line;
            _waiters.Add((match, tcs));
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        if (done != tcs.Task)
        {
            lock (_output) _waiters.RemoveAll(w => w.Done == tcs);
            throw new TimeoutException($"no matching console line within {timeout}. Last output:\n{string.Join('\n', OutputTail(20))}");
        }
        return await tcs.Task;
    }

    /// <summary>Runs a console command (no leading slash).</summary>
    public async Task CommandAsync(string command)
    {
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
    }

    public List<string> OutputTail(int n)
    {
        lock (_output) return _output.TakeLast(n).ToList();
    }

    private static readonly Lazy<Task<string>> Jar = new(DownloadJarAsync);

    private static Task<string> EnsureJarAsync() => Jar.Value;

    private static async Task<string> DownloadJarAsync()
    {
        Directory.CreateDirectory(CacheRoot);
        var jar = Path.Combine(CacheRoot, $"paper-{GameVersion}-{Build}.jar");
        if (File.Exists(jar) && Sha256(jar) == JarSha256) return jar;

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vertex-Tests/1.0 (https://github.com/LostBeard/Vertex)");
        using var meta = JsonDocument.Parse(await http.GetStringAsync(
            $"https://fill.papermc.io/v3/projects/paper/versions/{GameVersion}/builds/{Build}"));
        var url = meta.RootElement.GetProperty("downloads").GetProperty("server:default").GetProperty("url").GetString()!;
        var tmp = jar + ".download";
        await using (var src = await http.GetStreamAsync(url))
        await using (var dst = File.Create(tmp))
            await src.CopyToAsync(dst);
        var sha = Sha256(tmp);
        if (sha != JarSha256)
        {
            File.Delete(tmp);
            throw new InvalidDataException($"Paper jar SHA-256 mismatch: {sha}");
        }
        File.Move(tmp, jar, overwrite: true);
        return jar;
    }

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public async ValueTask DisposeAsync()
    {
        if (_process.HasExited) { _process.Dispose(); return; }
        try
        {
            await _process.StandardInput.WriteLineAsync("stop");
            await _process.StandardInput.FlushAsync();
        }
        catch (IOException) { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            // Our own process (by its PID), never by image name.
            _process.Kill(entireProcessTree: true);
        }
        _process.Dispose();
    }
}
