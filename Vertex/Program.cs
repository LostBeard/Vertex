using SpawnDev.Minecraft;
using SpawnDev.Minecraft.Auth;

// Vertex command line (the full app with the brain and voice comes later).
//   vertex login [account]                      sign a Microsoft account in (device code), cache it encrypted
//   vertex logout [account]                     forget the cached account
//   vertex ping <host[:port]>                   server list ping
//   vertex join <host[:port]> [account]         join with the signed-in account; stdin lines become chat
//   vertex join <host[:port]> --offline <name>  join an offline-mode server as <name>

const string App = "Vertex";

if (args.Length == 0)
{
    Console.WriteLine("usage: vertex login|logout [account] | ping <host[:port]> | join <host[:port]> [account | --offline <name>]");
    return 2;
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
