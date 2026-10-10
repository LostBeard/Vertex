using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Brain;

// Vertex command line.
//   vertex play <host[:port]> [--offline <name>] [--character <name>] [--model <ollama model>] [--account <name>]
//                                               play: brain, voice, reflexes. Lines typed here are said to Vertex.
//   vertex login [account]                      sign a Microsoft account in (device code), cache it encrypted
//   vertex logout [account]                     forget the cached account
//   vertex ping <host[:port]>                   server list ping
//   vertex join <host[:port]> [account]         join with the signed-in account; stdin lines become chat
//   vertex join <host[:port]> --offline <name>  join an offline-mode server as <name>

const string App = "Vertex";

if (args.Length == 0 || args[0] == "ui")
{
    // The dashboard: http://localhost:5280 (and on the LAN, so it can be opened from another device).
    return await RunDashboardAsync(args);
}
if (args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("usage: vertex [ui] | play <host[:port]> [--offline <name>] [--character <name>] [--model <model>] | login|logout [account] | ping <host[:port]> | join <host[:port]> [account | --offline <name>]");
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
        ?? throw new InvalidOperationException("no chat model found in the Ollama cache (ollama pull llama3.1:8b)");
    Log($"models: {string.Join(", ", models)}; using {model}");
    await ai.Chat.EnsureReadyAsync(model);

    var characters = new CharacterStore();
    var character = characters.Find(Option(args, "--character") ?? "Vertex") ?? Character.BuiltIn[0];
    await using var client = new MinecraftClient(profile, new ClientOptions { Log = Log, Brand = "vertex" });
    await using var voice = new SimpleVoiceChat(client, host);
    voice.Log += m => Log("[voice] " + m);
    await using var brain = new VertexBrain(client, voice, ai, characters, character, model);
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

static async Task<int> RunDashboardAsync(string[] args)
{
    var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
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
    Console.WriteLine("Vertex dashboard: http://localhost:5280");
    if (!args.Contains("--no-browser"))
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://localhost:5280") { UseShellExecute = true }); } catch { }
    await app.RunAsync();
    await app.Services.GetRequiredService<Vertex.Host.VertexHost>().DisposeAsync();
    return 0;
}
