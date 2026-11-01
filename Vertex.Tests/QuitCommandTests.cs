using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Vertex.Tests;

/// <summary>
/// `Vertex.exe quit`: the way an update (or a person at a console) closes a running Vertex properly. A silent install failed with Vertex running ("Setup has detected that Vertex is currently
/// running", exit code 1, nothing installed - seen 2026-11-01 on Aubs' PC). This runs the REAL exe as a second, isolated instance (its own mutex and quit-event name, port and data folder via
/// VERTEX_INSTANCE / VERTEX_DATA_FOLDER), so it never touches the user's own tray app that is running on the same PC.
/// </summary>
[NonParallelizable]
public class QuitCommandTests
{
    private string _data = "";
    private string _instance = "";
    private int _port;
    private Process? _tray;

    [SetUp]
    public void Isolate()
    {
        _instance = "t" + Guid.NewGuid().ToString("N")[..8];
        _data = Path.Combine(Path.GetTempPath(), "vertex-quit-" + _instance);
        Directory.CreateDirectory(_data);
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); _port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
    }

    [TearDown]
    public void Clean()
    {
        try { if (_tray is { HasExited: false }) _tray.Kill(entireProcessTree: true); } catch { }
        _tray?.Dispose();
        try { Directory.Delete(_data, true); } catch (IOException) { }
    }

    private static string Exe => Path.Combine(AppContext.BaseDirectory, "Vertex.exe");

    private Process Run(string arguments)
    {
        var psi = new ProcessStartInfo(Exe, arguments) { WorkingDirectory = AppContext.BaseDirectory, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["VERTEX_INSTANCE"] = _instance;
        psi.Environment["VERTEX_DATA_FOLDER"] = _data;
        return Process.Start(psi)!;
    }

    private bool TrayMutexExists()
    {
        try { Mutex.OpenExisting(@"Local\Vertex.Tray." + _instance).Dispose(); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    [Test]
    public async Task Quit_StopsARunningTray_AndItsPanel_AndASecondQuitSaysNothingIsRunning()
    {
        Assert.That(File.Exists(Exe), Is.True, "the test build has Vertex.exe beside the tests: " + Exe);
        _tray = Run($"--background --urls http://127.0.0.1:{_port}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var up = false;
        for (var until = DateTime.UtcNow.AddSeconds(60); DateTime.UtcNow < until && !up; await Task.Delay(500))
        {
            try { up = (await http.GetAsync($"http://127.0.0.1:{_port}/")).IsSuccessStatusCode && TrayMutexExists(); } catch { }
        }
        Assert.That(up, Is.True, "the isolated tray came up (exited: " + _tray.HasExited + ")");

        var sw = Stopwatch.StartNew();
        using (var quit = Run("quit"))
        {
            Assert.That(quit.WaitForExit(TimeSpan.FromSeconds(100)), Is.True, "`quit` returned");
            Assert.That(quit.ExitCode, Is.EqualTo(0), "`quit` says it worked");
        }
        TestContext.Out.WriteLine($"quit took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.That(_tray.WaitForExit(TimeSpan.FromSeconds(30)), Is.True, "the tray process is gone");
        Assert.That(TrayMutexExists(), Is.False, "the tray's mutex is released");
        var panelStillUp = false;
        try { panelStillUp = (await http.GetAsync($"http://127.0.0.1:{_port}/")).IsSuccessStatusCode; } catch { }
        Assert.That(panelStillUp, Is.False, "the panel no longer answers");

        using var again = Run("quit");
        Assert.That(again.WaitForExit(TimeSpan.FromSeconds(20)), Is.True);
        Assert.That(again.ExitCode, Is.EqualTo(0), "nothing running is not an error");
    }
}
