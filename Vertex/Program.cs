using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Brain;

// Vertex command line.
//   vertex play <host[:port]> [--offline <name>] [--character <name>] [--model <ollama model>] [--account <name>]
//                                               play: brain, voice, reflexes. Lines typed here are said to Vertex.
//   vertex install-models [model | --list]      install the language model (default llama3.1:8b) from models.spawndev.com; no Ollama needed
//   vertex login [account]                      sign a Microsoft account in (device code), cache it encrypted
//   vertex logout [account]                     forget the cached account
//   vertex ping <host[:port]>                   server list ping
//   vertex join <host[:port]> [account]         join with the signed-in account; stdin lines become chat
//   vertex join <host[:port]> --offline <name>  join an offline-mode server as <name>

const string App = "Vertex";

// No arguments (or --background, how Windows starts it at login): the tray app. Vertex.exe is a Windows app so that
// starting with Windows shows no console window; every other command writes to the terminal it was started from.
if (args.Length == 0 || args[0] is "--background" or "tray")
    return await RunTrayAsync(args);
// play and join read what is typed: a Windows app cannot share the terminal's input with the shell that started it,
// so they get a console window of their own. Everything else prints to the terminal it was run from.
if (args[0] is "play" or "join") AllocConsole();
else AttachParentConsole();
if (args[0] == "quit") return QuitRunning();
if (args[0] == "ui")
{
    // The dashboard alone, in this console: http://localhost:5280 (and on the LAN, so it can be opened from another device).
    return await RunDashboardAsync(args);
}
if (args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("usage: vertex [ui] | play <host[:port]> [--offline <name>] [--character <name>] [--model <model>] | install-models [model | --list] | login|logout [account] | ping <host[:port]> | join <host[:port]> [account | --offline <name>]");
    return 0;
}

try
{
    switch (args[0])
    {
        case "login":
        {
            var profile = await Auth(args.ElementAtOrDefault(1) ?? "default").SignInAsync();
            Console.WriteLine($"Signed in as {profile.Name} ({profile.Id})");
            return 0;
        }
        case "logout":
            await Auth(args.ElementAtOrDefault(1) ?? "default").SignOutAsync();
            Console.WriteLine("Signed out");
            return 0;
        case "ping":
        {
            var (host, port) = HostPort(args[1]);
            var status = await ServerStatus.PingAsync(host, port);
            Console.WriteLine($"{status.VersionName} (protocol {status.Protocol}), {status.PlayersOnline}/{status.PlayersMax} players, {status.Latency.TotalMilliseconds:0} ms");
            Console.WriteLine(status.Description);
            return 0;
        }
        case "join":
        {
            var (host, port) = HostPort(args[1]);
            var profile = args.ElementAtOrDefault(2) == "--offline"
                ? GameProfile.Offline(args[3])
                : await Auth(args.ElementAtOrDefault(2) ?? "default").SignInAsync();
            await using var client = new MinecraftClient(profile, new ClientOptions { Log = m => Console.WriteLine("[vertex] " + m) });
            var done = new TaskCompletionSource<string>();
            client.ChatReceived += m => Console.WriteLine(m.Text);
            client.Disconnected += r => done.TrySetResult(r);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult("Ctrl+C"); };
            await client.ConnectAsync(host, port);
            _ = Task.Run(async () =>
            {
                while (Console.ReadLine() is string line)
                    if (line.Length > 0 && client.State == ConnectionState.Play) await client.SendChatAsync(line);
            });
            Console.WriteLine("disconnected: " + await done.Task);
            return 0;
        }
        case "play":
            return await PlayAsync(args);
        case "install-models":
            return await InstallModelsAsync(args);
        default:
            Console.WriteLine("unknown command " + args[0]);
            return 2;
    }
}
catch (Exception e)
{
    Console.WriteLine("error: " + e.Message);
    return 1;
}

// Names the tray's mutex and quit event, so a test (or a second copy) can run beside the user's own: VERTEX_INSTANCE=name. Normally empty.
static string Instance() => Environment.GetEnvironmentVariable("VERTEX_INSTANCE") is { Length: > 0 } s ? "." + s : "";

// vertex quit: asks the running tray app to quit (it stops Vertex first: the robot is parked, the game left) and waits until it is gone. Local to this Windows session.
static int QuitRunning()
{
    EventWaitHandle ask;
    try { ask = EventWaitHandle.OpenExisting(@"Local\Vertex.Quit" + Instance()); }
    catch (WaitHandleCannotBeOpenedException) { Console.WriteLine("Vertex is not running"); return 0; }
    using (ask) ask.Set();
    var until = DateTime.UtcNow.AddSeconds(90);
    while (DateTime.UtcNow < until)
    {
        try { Mutex.OpenExisting(@"Local\Vertex.Tray" + Instance()).Dispose(); }   // the tray holds this until its process ends
        catch (WaitHandleCannotBeOpenedException) { Console.WriteLine("Vertex has quit"); return 0; }
        Thread.Sleep(300);
    }
    Console.WriteLine("Vertex did not quit within 90 seconds");
    return 1;
}

// vertex install-models [model | --list]: installs the language model (default llama3.1:8b) from models.spawndev.com, then registry.ollama.ai, with no Ollama needed.
static async Task<int> InstallModelsAsync(string[] args)
{
    if (args.Contains("--list"))
    {
        var have = ModelSetup.Installed();
        foreach (var m in await ModelSetup.ListInstallableAsync())
            Console.WriteLine($"{m.Name,-30} {ModelSetup.Size(m.SizeBytes),8}  {(have.Contains(m.Name) ? "installed" : ""),-9} {m.Description}");
        return 0;
    }
    var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? ModelSetup.DefaultModel;
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var last = "";
    try
    {
        var model = await ModelSetup.InstallAsync(name, p =>
        {
            var text = ModelSetup.Describe(p);
            if (text != last) { last = text; Console.Write("\r" + text.PadRight(110)); }
        }, cts.Token);
        Console.WriteLine();
        Console.WriteLine($"installed {model.Name}");
        return 0;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine();
        Console.WriteLine("stopped; what was downloaded is kept, run the same command again to carry on");
        return 130;
    }
}

static MicrosoftAuth Auth(string account) => new(DpapiAccountStore.ForAccount(App, account), prompt =>
{
    Console.WriteLine(prompt.ToString());
    return Task.CompletedTask;
});

static (string Host, int Port) HostPort(string value)
{
    var i = value.LastIndexOf(':');
    return i > 0 ? (value[..i], int.Parse(value[(i + 1)..])) : (value, 25565);
}

static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static async Task<int> PlayAsync(string[] args)
{
    var (host, port) = HostPort(args[1]);
    var profile = Option(args, "--offline") is string offline
        ? GameProfile.Offline(offline)
        : await Auth(Option(args, "--account") ?? "default").SignInAsync();
    void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");

    await using var ai = new VertexAi(Log);
    var models = await ai.ModelNamesAsync();
    var model = Option(args, "--model") ?? models.FirstOrDefault(m => m.StartsWith("llama3.1")) ?? models.FirstOrDefault()
        ?? throw new InvalidOperationException($"no language model is installed yet: run `vertex install-models` ({ModelSetup.DefaultModel}, from models.spawndev.com)");
    Log($"models: {string.Join(", ", models)}; using {model}");
    await ai.EnsureChatReadyAsync(model);
    try { await ai.LoadIntentsAsync(); }
    catch (Exception e) { Log("fast commands unavailable: " + e.Message); }

    var characters = new CharacterStore();
    var character = characters.Find(Option(args, "--character") ?? "Vertex") ?? Character.BuiltIn[0];
    await using var client = new MinecraftClient(profile, new ClientOptions { Log = Log, Brand = "vertex" });
    await using var voice = new SimpleVoiceChat(client, host);
    voice.Log += m => Log("[voice] " + m);
    await using var brain = new VertexBrain(client, voice, ai, characters, character, model, $"{host}_{port}");
    brain.Log += Log;
    var done = new TaskCompletionSource<string>();
    client.ChatReceived += m => Log("[chat] " + m.Text);
    client.Disconnected += r => done.TrySetResult(r);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult("Ctrl+C"); };
    await client.ConnectAsync(host, port);
    brain.Start();
    Log($"playing as {character.Name} ({profile.Name})");
    _ = Task.Run(() =>
    {
        // The console is a player too: what is typed here is said to Vertex.
        while (Console.ReadLine() is string line)
            if (line.Length > 0) brain.Post(new SaidEvent(Guid.Empty, "Console", line, false));
    });
    Log("disconnected: " + await done.Task);
    return 0;
}

static Microsoft.AspNetCore.Builder.WebApplication BuildControlPanel(string[] args)
{
    // The content root is the folder Vertex.exe is in, NOT the current directory: Windows starts it at login (the Run key) and from shortcuts with
    // some other folder current, and then the framework's static files (blazor.web.js) were found by nobody and served as 200 with 0 bytes.
    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
    builder.WebHost.UseUrls(Option(args, "--urls") ?? "http://0.0.0.0:5280");
    // Serve the framework's static files (blazor.web.js) when running from the build output, not only when published
    // or in Development: without them the page renders but nothing on it responds.
    builder.WebHost.UseStaticWebAssets();
    builder.Services.AddSingleton<Vertex.Host.VertexHost>();
    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    var app = builder.Build();
    app.UseAntiforgery();
    app.MapStaticAssets(); // serves _framework/blazor.web.js (without it the page renders but nothing is clickable)
    app.MapRazorComponents<Vertex.Components.App>().AddInteractiveServerRenderMode();
    // The live game as data (read-only): how he is and what he sees, without driving the Blazor page.
    app.MapGet("/state", (Vertex.Host.VertexHost host) => host.Brain is { } brain ? Microsoft.AspNetCore.Http.Results.Json(StateDump.Snapshot(brain)) : Microsoft.AspNetCore.Http.Results.NotFound("not in a game"));
    app.MapGet("/map", (Vertex.Host.VertexHost host, int? r, bool? heights) => host.Brain is { } brain ? Microsoft.AspNetCore.Http.Results.Text(StateDump.Map(brain, r ?? 48, heights == true)) : Microsoft.AspNetCore.Http.Results.NotFound("not in a game"));
    return app;
}

static async Task<int> RunDashboardAsync(string[] args)
{
    var app = BuildControlPanel(args);
    Console.WriteLine("Vertex dashboard: http://localhost:5280");
    if (!args.Contains("--no-browser")) Vertex.Host.VertexTray.OpenControlPanel();
    await app.RunAsync();
    await app.Services.GetRequiredService<Vertex.Host.VertexHost>().DisposeAsync();
    return 0;
}

// The tray app: the control panel's web host plus an icon in the notification area. One per user session; launching
// it again just opens the control panel.
static async Task<int> RunTrayAsync(string[] args)
{
    using var single = new Mutex(initiallyOwned: true, @"Local\Vertex.Tray" + Instance(), out var first);
    if (!first)
    {
        // Already running: a person launching it again wants the control panel; a background start wants nothing.
        if (!args.Contains("--background")) Vertex.Host.VertexTray.OpenControlPanel();
        return 0;
    }
    var app = BuildControlPanel(args);
    await app.StartAsync();
    var host = app.Services.GetRequiredService<Vertex.Host.VertexHost>();
    // What he did, for looking at later; how he is, every 10 minutes, in the game or not (a gap in these is the app not running).
    var playLog = new Vertex.Host.PlayLog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex", "logs" + Instance()));
    host.PlayLog = playLog;
    using var statusTimer = new System.Threading.Timer(_ =>
    {
        object? state = null;
        try { if (host.Brain is { } brain) state = StateDump.Snapshot(brain); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or KeyNotFoundException) { state = new { snapshotFailed = e.Message }; }
        playLog.Status(DateTime.Now, state);
    }, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(10));
    Vertex.Host.VertexTray? trayContext = null;
    var tray = new Thread(() =>
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        var context = new Vertex.Host.VertexTray(host, () => app.Lifetime.StopApplication());
        trayContext = context;
        System.Windows.Forms.Application.Run(context);
    }) { Name = "Vertex tray" };
    tray.SetApartmentState(ApartmentState.STA); // Windows Forms needs a single-threaded apartment
    tray.Start();
    // `vertex quit` (and an update) asks through a named event in this Windows session: the tray stops Vertex properly (the robot is parked, the game left) and exits.
    using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Vertex.Quit" + Instance());
    ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => trayContext?.RequestQuit(), null, Timeout.Infinite, executeOnlyOnce: false);
    // Started by a person: show the control panel. Started by Windows at login (--background): just the icon.
    if (!args.Contains("--background")) Vertex.Host.VertexTray.OpenControlPanel();
    // "Start Vertex when the app starts" (a PC that is Vertex's own): the same Start the button does, in the background so the panel and the icon are up at once.
    _ = host.StartOnLaunchAsync();
    await app.WaitForShutdownAsync();
    await host.DisposeAsync();
    await app.DisposeAsync();
    return 0;
}

// Vertex.exe is a Windows (not console) app; a command run from a terminal writes its output back to that terminal.
static void AttachParentConsole()
{
    if (AttachConsole(-1))
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool AttachConsole(int processId);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern bool AllocConsole();
