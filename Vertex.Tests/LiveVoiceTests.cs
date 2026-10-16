using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// M5 voice against a REAL Paper server running the REAL Simple Voice Chat plugin (the voicechat-bukkit 2.6.16 jar
/// AubsCraft ships): two Vertex clients connect voice, one speaks a known signal, the other must hear it from the
/// right player, decoded, with the right pitch, then the stop.
/// </summary>
[NonParallelizable]
public class LiveVoiceTests
{
    private const int Port = 25932;
    private const int VoicePort = 25933;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    /// <summary>
    /// The Simple Voice Chat Bukkit plugin: VERTEX_VOICECHAT_JAR, or the copy next to the AubsCraft repo (found relative to the test folder, which only works for a build inside the repo),
    /// or the newest one in the AubsCraft test servers' plugin folders (%LOCALAPPDATA%\AubsCraft.Tests\servers), where the AubsCraft tests keep theirs.
    /// </summary>
    private static readonly string PluginJar = FindPluginJar();

    private static string FindPluginJar()
    {
        var env = Environment.GetEnvironmentVariable("VERTEX_VOICECHAT_JAR");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        // Walk up from wherever the tests were built to a folder that has the AubsCraft repo beside it (a build in /tmp is not inside the repo).
        var beside = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "..", "AubsCraft", "voicechat-bukkit-2.6.16.jar"));
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "AubsCraft", "voicechat-bukkit-2.6.16.jar");
            if (File.Exists(candidate)) return candidate;
        }
        if (File.Exists(beside)) return beside;
        var projects = Environment.GetEnvironmentVariable("VERTEX_PROJECTS_DIR");
        if (!string.IsNullOrEmpty(projects) && Directory.EnumerateFiles(Path.Combine(projects, "AubsCraft"), "voicechat-bukkit-*.jar").FirstOrDefault() is { } inProjects) return inProjects;
        var servers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AubsCraft.Tests", "servers");
        if (Directory.Exists(servers))
        {
            var found = Directory.EnumerateFiles(servers, "voicechat-bukkit-*.jar", SearchOption.AllDirectories)
                .Where(f => !f.Contains(".paper-remapped") && !f.Contains(Path.DirectorySeparatorChar + "vertex-"))
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (found != null) return found;
        }
        return beside;
    }
    private PaperServer _server = null!;

    [OneTimeSetUp]
    public async Task StartServer()
    {
        Assert.That(File.Exists(PluginJar), "plugin jar not found: " + PluginJar);
        _server = await PaperServer.StartAsync("voice", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" },
            dir =>
            {
                var plugins = Path.Combine(dir, "plugins");
                Directory.CreateDirectory(Path.Combine(plugins, "voicechat"));
                File.Copy(PluginJar, Path.Combine(plugins, Path.GetFileName(PluginJar)), overwrite: true);
                File.WriteAllText(Path.Combine(plugins, "voicechat", "voicechat-server.properties"), $"port={VoicePort}\n");
                return Task.CompletedTask;
            });
        await _server.WaitForLineAsync(l => l.Contains("Voice chat server started at"), Timeout, includeHistory: true);
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        if (_server != null) await _server.DisposeAsync();
    }

    private static short[] Tone(double hz, double seconds)
    {
        var n = (int)(SimpleVoiceChat.SampleRate * seconds);
        var pcm = new short[n];
        for (var i = 0; i < n; i++) pcm[i] = (short)(Math.Sin(2 * Math.PI * hz * i / SimpleVoiceChat.SampleRate) * 12000);
        return pcm;
    }

    /// <summary>Signal power at one frequency (Goertzel).</summary>
    private static double Power(short[] pcm, double hz)
    {
        var k = 2 * Math.Cos(2 * Math.PI * hz / SimpleVoiceChat.SampleRate);
        double s1 = 0, s2 = 0;
        foreach (var x in pcm)
        {
            var s0 = x + k * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - k * s1 * s2;
    }

    [Test]
    public async Task Speak_IsHeardByAnotherPlayer()
    {
        await using var alice = new MinecraftClient(GameProfile.Offline("VertexTalk"));
        await using var bob = new MinecraftClient(GameProfile.Offline("VertexHear"));
        await using var aliceVoice = new SimpleVoiceChat(alice, "127.0.0.1");
        await using var bobVoice = new SimpleVoiceChat(bob, "127.0.0.1");
        var log = new ConcurrentQueue<string>();
        aliceVoice.Log += m => log.Enqueue("alice: " + m);
        bobVoice.Log += m => log.Enqueue("bob: " + m);
        var heard = new ConcurrentQueue<VoiceFrame>();
        bobVoice.AudioReceived += heard.Enqueue;

        using var cts = new CancellationTokenSource(Timeout);
        await alice.ConnectAsync("127.0.0.1", Port, cts.Token);
        await bob.ConnectAsync("127.0.0.1", Port, cts.Token);
        try
        {
            await aliceVoice.WaitConnectedAsync(cts.Token);
            await bobVoice.WaitConnectedAsync(cts.Token);
        }
        catch (Exception e)
        {
            Assert.Fail($"voice did not connect: {e.Message}\n{string.Join('\n', log)}\n{string.Join('\n', _server.OutputTail(20))}");
        }
        Assert.That(aliceVoice.Codec, Is.EqualTo("VOIP"));
        Assert.That(aliceVoice.VoiceDistance, Is.EqualTo(48));

        // The server tells everyone who is on voice.
        await Wait.UntilAsync(() => bobVoice.PlayerStates.TryGetValue(alice.Profile.Id, out var s) && !s.Disconnected, Timeout,
            () => string.Join(", ", bobVoice.PlayerStates.Values));

        // 0.6 s of 440 Hz then 0.6 s of 880 Hz.
        var speech = Tone(440, 0.6).Concat(Tone(880, 0.6)).ToArray();
        await aliceVoice.SpeakAsync(speech);
        await Wait.UntilAsync(() => heard.Any(f => f.Pcm.Length == 0), Timeout, () => $"{heard.Count} frames, no stop");

        var frames = heard.Where(f => f.Pcm.Length > 0).ToList();
        TestContext.Out.WriteLine($"{frames.Count} frames heard, {frames.Sum(f => f.Pcm.Length)} samples, distance {frames.FirstOrDefault()?.Distance}");
        foreach (var line in log) TestContext.Out.WriteLine(line);
        Assert.That(frames.All(f => f.Sender == alice.Profile.Id), "every frame is from Alice");
        Assert.That(frames.All(f => f.Kind == VoiceSoundKind.Player), "proximity (player) sound");
        Assert.That(frames.Count, Is.InRange(55, 61), "60 frames of 20 ms sent");
        var pcm = frames.OrderBy(f => f.Sequence).SelectMany(f => f.Pcm).ToArray();
        var firstHalf = pcm.Take(pcm.Length / 2 - 4800).ToArray();
        var secondHalf = pcm.Skip(pcm.Length / 2 + 4800).ToArray();
        // Opus is lossy but a pure tone survives: each half is dominated by its own frequency.
        Assert.That(Power(firstHalf, 440), Is.GreaterThan(Power(firstHalf, 880) * 50), "first half is 440 Hz");
        Assert.That(Power(secondHalf, 880), Is.GreaterThan(Power(secondHalf, 440) * 50), "second half is 880 Hz");
    }
}
