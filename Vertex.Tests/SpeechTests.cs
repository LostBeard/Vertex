using System.Collections.Concurrent;
using SpawnDev.Minecraft;
using SpawnDev.Minecraft.VoiceChat;
using Vertex.AI;
using Vertex.Core;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// Vertex's ears and mouth on the GPU (SpawnDev.ILGPU.ML through SpawnDev.AI.Server): Kokoro speaks, Whisper
/// listens. First directly, then through a REAL Paper server running the REAL Simple Voice Chat plugin: one Vertex
/// says a sentence in proximity voice and another Vertex writes down what it heard.
/// Category GPU: needs CUDA and the speech models (downloaded once from the SpawnDev hub, then cached).
/// </summary>
[Category("GPU")]
[NonParallelizable]
public class SpeechTests
{
    private const int Port = 25934;
    private const int VoicePort = 25935;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private VertexAi _ai = null!;

    [OneTimeSetUp]
    public void CreateAi() => _ai = TestAi.Create();

    [OneTimeTearDown]
    public async Task DisposeAi()
    {
        if (_ai != null) await _ai.DisposeAsync();
    }

    private static int WordsInCommon(string said, string heard)
    {
        static string[] Words(string s) => s.ToLowerInvariant().Split([' ', ',', '.', '!', '?', '\''], StringSplitOptions.RemoveEmptyEntries);
        var h = Words(heard).ToHashSet();
        return Words(said).Count(h.Contains);
    }

    [Test]
    public async Task KokoroSpeaks_WhisperUnderstands()
    {
        const string line = "Let's build a castle next to the river.";
        var (samples, rate) = await _ai.SpeakAsync(line, "kokoro:af_heart");
        Assert.That(rate, Is.EqualTo(24000));
        Assert.That(samples.Length / 24000.0, Is.InRange(1.0, 6.0), "a couple of seconds of speech");

        // The same path voice chat takes: 24 kHz -> 48 kHz shorts -> 16 kHz floats.
        var heard = await _ai.TranscribeAsync(Audio.Downsample48To16(Audio.Upsample24To48(samples)));
        TestContext.Out.WriteLine($"said: {line}\nheard: {heard}");
        Assert.That(WordsInCommon(line, heard), Is.GreaterThanOrEqualTo(6), heard);
    }

    [Test]
    public async Task OneVertexSpeaks_AnotherHearsTheWords()
    {
        var pluginJar = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "..", "AubsCraft", "voicechat-bukkit-2.6.16.jar"));
        await using var server = await PaperServer.StartAsync("speech", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" },
            dir =>
            {
                Directory.CreateDirectory(Path.Combine(dir, "plugins", "voicechat"));
                File.Copy(pluginJar, Path.Combine(dir, "plugins", "voicechat-bukkit-2.6.16.jar"), overwrite: true);
                File.WriteAllText(Path.Combine(dir, "plugins", "voicechat", "voicechat-server.properties"), $"port={VoicePort}\n");
                return Task.CompletedTask;
            });

        await using var speaker = new MinecraftClient(GameProfile.Offline("VertexMouth"));
        await using var listener = new MinecraftClient(GameProfile.Offline("VertexEars"));
        await using var speakerVoice = new SimpleVoiceChat(speaker, "127.0.0.1");
        await using var listenerVoice = new SimpleVoiceChat(listener, "127.0.0.1");
        using var cts = new CancellationTokenSource(Timeout);
        await speaker.ConnectAsync("127.0.0.1", Port, cts.Token);
        await listener.ConnectAsync("127.0.0.1", Port, cts.Token);
        await speakerVoice.WaitConnectedAsync(cts.Token);
        await listenerVoice.WaitConnectedAsync(cts.Token);

        using var ears = new Ears(listenerVoice, _ai.TranscribeAsync);
        var heard = new ConcurrentQueue<HeardSpeech>();
        ears.Heard += heard.Enqueue;
        ears.Log += m => TestContext.Out.WriteLine("[ears] " + m);
        await using var game = new MinecraftChannel(speaker, speakerVoice, _ai.TranscribeAsync) { EchoText = false };
        await using var mouth = new Mouth(_ai.SpeakAsync);
        mouth.Log += m => TestContext.Out.WriteLine("[mouth] " + m);

        const string line = "Hello Aubri, follow me to the cave.";
        mouth.Say(line, [game]);
        await Wait.UntilAsync(() => !heard.IsEmpty, Timeout, () => "nothing heard");
        var got = heard.First();
        TestContext.Out.WriteLine($"said: {line}\nheard: {got.Text} ({got.Seconds:0.0} s)");
        Assert.That(got.Speaker, Is.EqualTo(speaker.Profile.Id));
        Assert.That(WordsInCommon(line, got.Text), Is.GreaterThanOrEqualTo(5), got.Text);
    }
}
