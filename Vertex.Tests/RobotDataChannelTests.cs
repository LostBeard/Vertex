using System.Text.Json;
using SpawnDev.Reachy;
using SpawnDev.RTC;
using SpawnDev.SpawnJS.JSObjects;

namespace Vertex.Tests;

/// <summary>
/// Audio to the robot over its data channel, against the wire protocol read from the daemon's source (upload_audio_start / _chunk / _finish, play_uploaded_audio, cancel_audio).
/// A stand-in channel that records what is sent and answers like the daemon.
/// </summary>
public class RobotDataChannelTests
{
    private sealed class FakeChannel : IRTCDataChannel
    {
        public readonly List<JsonElement> Sent = new();
        public string ReadyState { get; set; } = "open";
        public long BufferedAmount { get; set; }
        public Func<JsonElement, string?>? Reply;       // what the "daemon" answers to a command, or null
        public event Action<string>? OnStringMessage;

        public void Send(string data)
        {
            var message = JsonDocument.Parse(data).RootElement.Clone();
            lock (Sent) Sent.Add(message);
            var reply = Reply?.Invoke(message);
            if (reply != null) Task.Run(() => OnStringMessage?.Invoke(reply));
        }
        public void Receive(string text) => OnStringMessage?.Invoke(text);

        public string Label => "data"; public ushort? Id => 1; public bool Ordered => true; public string Protocol => ""; public bool Negotiated => false;
        public ushort? MaxPacketLifeTime => null; public ushort? MaxRetransmits => null;
        public long BufferedAmountLowThreshold { get; set; }
        public string BinaryType { get; set; } = "arraybuffer";
        public void Send(byte[] data) => throw new NotSupportedException();
        public void Send(ArrayBuffer data) => throw new NotSupportedException();
        public void Send(TypedArray data) => throw new NotSupportedException();
        public void Send(Blob data) => throw new NotSupportedException();
        public void Send(DataView data) => throw new NotSupportedException();
        public void Close() { }
        public void Dispose() { }
        public event Action? OnOpen { add { } remove { } }
        public event Action? OnClose { add { } remove { } }
        public event Action? OnClosing { add { } remove { } }
        public event Action? OnBufferedAmountLow { add { } remove { } }
        public event Action<byte[]>? OnBinaryMessage { add { } remove { } }
        public event Action<ArrayBuffer>? OnArrayBufferMessage { add { } remove { } }
        public event Action<string>? OnError { add { } remove { } }
    }

    private static string Type(JsonElement m) => m.GetProperty("type").GetString()!;

    [Test]
    public async Task AClipGoesUpAsStartChunksFinish_InOrder_AndTheChunksAreTheWholeBase64()
    {
        var channel = new FakeChannel();
        var robot = new RobotDataChannel(channel);
        var wav = new byte[100_000];
        new Random(3).NextBytes(wav);
        var id = await robot.UploadAudioAsync(wav);

        var sent = channel.Sent.ToList();
        var base64 = Convert.ToBase64String(wav);
        var expectedChunks = (base64.Length + RobotDataChannel.ChunkChars - 1) / RobotDataChannel.ChunkChars;
        Assert.That(Type(sent[0]), Is.EqualTo("upload_audio_start"));
        Assert.That(sent[0].GetProperty("upload_id").GetString(), Is.EqualTo(id));
        Assert.That(sent[0].GetProperty("total_chunks").GetInt32(), Is.EqualTo(expectedChunks));
        Assert.That(sent[0].GetProperty("encoding").GetString(), Is.EqualTo("wav-base64"));
        Assert.That(Type(sent[^1]), Is.EqualTo("upload_audio_finish"));
        Assert.That(sent.Count, Is.EqualTo(expectedChunks + 2));
        var chunks = sent.Skip(1).Take(expectedChunks).ToList();
        for (var i = 0; i < chunks.Count; i++)
        {
            Assert.That(Type(chunks[i]), Is.EqualTo("upload_audio_chunk"));
            Assert.That(chunks[i].GetProperty("chunk_index").GetInt32(), Is.EqualTo(i), "the daemon drops a slot whose chunks arrive out of order");
            Assert.That(chunks[i].GetProperty("chunk").GetString()!.Length, Is.LessThanOrEqualTo(RobotDataChannel.ChunkChars));
        }
        Assert.That(string.Concat(chunks.Select(c => c.GetProperty("chunk").GetString())), Is.EqualTo(base64), "the chunks make up the file");
        Assert.That(Convert.FromBase64String(string.Concat(chunks.Select(c => c.GetProperty("chunk").GetString()))), Is.EqualTo(wav));
    }

    [Test]
    public async Task PlayingWaitsForTheRobotsStartedBroadcast()
    {
        var channel = new FakeChannel();
        channel.Reply = m => Type(m) == "play_uploaded_audio"
            ? $"{{\"type\":\"play_uploaded_audio\",\"upload_id\":\"{m.GetProperty("upload_id").GetString()}\",\"started\":true}}" : null;
        var robot = new RobotDataChannel(channel);
        await robot.PlayUploadedAudioAsync("u123");                      // returns once the broadcast arrives
        Assert.That(channel.Sent.Single().GetProperty("upload_id").GetString(), Is.EqualTo("u123"));
    }

    [Test]
    public void AnotherClipsBroadcastIsNotMine_AndNoAnswerTimesOut()
    {
        var channel = new FakeChannel();
        channel.Reply = m => "{\"type\":\"play_uploaded_audio\",\"upload_id\":\"someone-else\",\"started\":true}";
        var robot = new RobotDataChannel(channel);
        Assert.ThrowsAsync<TimeoutException>(async () => await robot.PlayUploadedAudioAsync("u123", TimeSpan.FromMilliseconds(200)));
    }

    [Test]
    public void ARefusalFromTheRobotIsAnError()
    {
        var channel = new FakeChannel();
        channel.Reply = m => "{\"type\":\"play_uploaded_audio\",\"upload_id\":\"u9\",\"error\":\"no such uploaded audio\"}";
        var robot = new RobotDataChannel(channel);
        var e = Assert.ThrowsAsync<InvalidOperationException>(async () => await robot.PlayUploadedAudioAsync("u9"));
        Assert.That(e!.Message, Does.Contain("no such uploaded audio"));
    }

    [Test]
    public async Task ACancelIsSentForThatClip_AndAClosedChannelRefusesToUpload()
    {
        var channel = new FakeChannel();
        var robot = new RobotDataChannel(channel);
        Assert.That(robot.CancelAudio("u7"), Is.True);
        Assert.That(Type(channel.Sent.Single()), Is.EqualTo("cancel_audio"));
        channel.ReadyState = "closed";
        Assert.That(robot.IsOpen, Is.False);
        Assert.That(robot.CancelAudio("u7"), Is.False, "nothing is sent on a closed channel");
        Assert.ThrowsAsync<InvalidOperationException>(async () => await robot.UploadAudioAsync(new byte[10]));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ABackedUpChannelMakesTheUploadWaitForTheBufferToDrain()
    {
        var channel = new FakeChannel { BufferedAmount = 2 * 1024 * 1024 };      // over the high-water mark
        var robot = new RobotDataChannel(channel);
        var upload = robot.UploadAudioAsync(new byte[50_000]);
        await Task.Delay(150);
        Assert.That(upload.IsCompleted, Is.False, "it must wait while the buffer is full");
        Assert.That(channel.Sent.Count, Is.EqualTo(1), "only the start message went out");
        channel.BufferedAmount = 0;
        await upload;
        Assert.That(Type(channel.Sent.Last()), Is.EqualTo("upload_audio_finish"));
    }
}
