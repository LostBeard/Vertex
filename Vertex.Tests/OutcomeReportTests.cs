using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SpawnDev.Minecraft;
using Vertex.AI;
using Vertex.Brain;
using Vertex.Tests.Live;

namespace Vertex.Tests;

/// <summary>
/// 2026-11-01, live on AubsCraft: a planting job failed ("I don't have any carrot to plant") and he told everyone "I finished planting a field of carrots" while he stood still. The text he
/// was given said "you finished planting a field of carrots... Result: I don't have any carrot", for a failure as much as for a success, and a small model reads the first words.
/// </summary>
public class OutcomeReportTests
{
    private static readonly TaskOutcome Failed = new("planting a field of carrots", false, "I don't have any carrot to plant.");
    private static readonly TaskOutcome Worked = new("exploring north", true, "I explored 40 blocks north and found 1 cow.");

    [Test]
    public void AFailedJob_SaysItDidNotWork_AndNeverSaysItWasFinished()
    {
        foreach (var goal in new string?[] { null, "plant some carrots" })
        {
            var text = OutcomeReport.ForModel(Failed, goal);
            Assert.That(text, Does.Contain("did NOT work").And.Contain("nothing was done").And.Contain("I don't have any carrot to plant"));
            Assert.That(text, Does.Not.Contain("you finished"), "a failure is not 'finished'");
            Assert.That(text, Does.Contain("Say only what this says"));
        }
        Assert.That(OutcomeReport.ForModel(Failed, "plant some carrots"), Does.Contain("finish_goal").And.Contain("no longer wanted"), "he is told he may give up a goal he cannot do");
    }

    [Test]
    public void AJobThatWorked_StillSaysFinished_WithItsResult_AndAsksIfTheGoalIsReached()
    {
        var plain = OutcomeReport.ForModel(Worked, null);
        Assert.That(plain, Does.StartWith("you finished exploring north").And.Contain("I explored 40 blocks north and found 1 cow").And.Contain("Say only what this says"));
        Assert.That(plain, Does.Not.Contain("finish_goal"));
        var forGoal = OutcomeReport.ForModel(Worked, "find a village");
        Assert.That(forGoal, Does.Contain("a step toward your goal \"find a village\"").And.Contain("Is the goal reached now?").And.Contain("finish_goal"));
    }

    [Test]
    public async Task AReflexJob_IsQuiet_AndIsNotTheBrainBeingGivenSomethingToDo()
    {
        var runner = new TaskRunner();
        var started = new List<string>();
        runner.Starting += started.Add;
        var done = new TaskCompletionSource<TaskOutcome>();
        runner.Finished += o => done.TrySetResult(o);
        runner.StartReflex("stepping away from monsters", _ => Task.FromResult((false, "I could not get away from the monsters.")));
        var outcome = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(outcome.Quiet, Is.True, "a reflex's ending is not spoken about");
        Assert.That(outcome.For, Is.Null, "done for nobody in particular");
        Assert.That(outcome.Goal, Is.Null, "and it is no step of a goal");
        Assert.That(started, Is.Empty, "and it is not 'being given something to do', so a 'stay here' stays in force");

        var normal = new TaskCompletionSource<TaskOutcome>();
        var runner2 = new TaskRunner();
        runner2.Finished += o => normal.TrySetResult(o);
        runner2.Start("building a house", _ => Task.FromResult((false, "no")));
        Assert.That((await normal.Task.WaitAsync(TimeSpan.FromSeconds(10))).Quiet, Is.False, "an ordinary job is still reported");
    }

    [Test]
    public void TheProjectsOffered_AreOnlyOnesHeCanDoWithWhatHeCarries()
    {
        var empty = VertexBrain.OwnProjectIdeas(_ => 0);
        Assert.That(empty, Has.Count.EqualTo(3));
        Assert.That(string.Join(" | ", empty), Does.Contain("build").And.Contain("explore").And.Contain("gather").And.Not.Contain("plant").And.Not.Contain("fishing"), "no seeds, no rod: nothing to plant or fish with");
        var seeds = VertexBrain.OwnProjectIdeas(item => item == "wheat_seeds" ? 7 : 0);
        Assert.That(string.Join(" | ", seeds), Does.Contain("plant the seeds or crops you carry").And.Not.Contain("fishing"));
        var rod = VertexBrain.OwnProjectIdeas(item => item == "fishing_rod" ? 1 : 0);
        Assert.That(string.Join(" | ", rod), Does.Contain("go fishing").And.Not.Contain("plant"));
        Assert.That(VertexBrain.OwnProjectIdeas(item => item == "carrot" ? 3 : 0).Any(i => i.Contains("plant")), Is.True, "carrots are crops to plant too");
    }
}

/// <summary>The real thing: a job FAILS in a real world, the real model is told, and what he says to his friends must not claim it was done. Repeated because a model is not deterministic.</summary>
[Category("GPU")]
[NonParallelizable]
public class HonestReportTests
{
    private const int Port = 25984;
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(3);
    private PaperServer _server = null!;
    private VertexAi _ai = null!;
    private string _model = "";
    private readonly List<string> _folders = [];

    [OneTimeSetUp]
    public async Task Start()
    {
        _server = await PaperServer.StartAsync("honest", Port, TimeSpan.FromMinutes(4), new Dictionary<string, string> { ["difficulty"] = "peaceful" });
        await _server.CommandAsync("gamerule doMobSpawning false");
        _ai = TestAi.Create();
        _model = TestModel.Pick(await _ai.ModelNamesAsync());
        await _ai.EnsureChatReadyAsync(_model);
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        if (_ai != null) await _ai.DisposeAsync();
        if (_server != null) await _server.DisposeAsync();
        foreach (var f in _folders) { try { if (Directory.Exists(f)) Directory.Delete(f, true); } catch (IOException) { } }
    }

    /// <summary>Claims that it was done: "I finished planting", "I planted a field", "I have completed". Not "I couldn't finish", not "I don't have any".</summary>
    private static readonly Regex ClaimsDone = new(@"\bI(?:'ve| have)?\s+(?:just\s+|now\s+)?(?:finished|planted|completed|done)\b|\b(?:all done|is done|are planted|have been planted)\b", RegexOptions.IgnoreCase);

    [Test]
    public async Task AFailedJob_IsNotReportedToHisFriendsAsDone([Values(1, 2, 3, 4)] int attempt)
    {
        var log = new ConcurrentQueue<string>();
        var client = new MinecraftClient(GameProfile.Offline("VertexHonest" + attempt));
        using var cts = new CancellationTokenSource(Slow);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var root = Path.Combine(Path.GetTempPath(), $"vertex-tests-honest-{Guid.NewGuid():N}");
        _folders.Add(root);
        await using var brain = new VertexBrain(client, null, _ai, new CharacterStore(Path.Combine(root, "characters")), Character.BuiltIn[0], _model);
        await using var _c = client;
        // A friend in the world, listening: what Vertex says is what arrives in chat, not anything the brain logs.
        await using var friend = new MinecraftClient(GameProfile.Offline("Friend" + attempt));
        await friend.ConnectAsync("127.0.0.1", Port, cts.Token);
        var heard = new ConcurrentQueue<ChatMessage>();
        friend.ChatReceived += heard.Enqueue;
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Start();

        // A job that fails the way the carrot job did on the live server.
        brain.Tasks.Start("planting a field of carrots", _ => Task.FromResult((false, "I don't have any carrot to plant. (Breaking tall grass gives wheat seeds; carrots and potatoes come from villagers and zombies.)")));
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("task ended:")), Slow, () => "no task end; log: " + string.Join(" | ", log.TakeLast(6)));
        await Wait.UntilAsync(() => heard.Any(m => m.SenderId == client.Profile.Id), Slow, () => "he never said anything about it; log: " + string.Join(" | ", log.TakeLast(6)));
        await Task.Delay(TimeSpan.FromSeconds(3)); // a long answer arrives in chunks
        var said = string.Join(" ", heard.Where(m => m.SenderId == client.Profile.Id).Select(m => m.Content));
        TestContext.Out.WriteLine("HE SAID: " + said);
        Assert.That(ClaimsDone.IsMatch(said), Is.False, "he told his friends it was done when the job failed: " + said);
    }

    /// <summary>
    /// Live on AubsCraft 2026-11-02: a reflex's failed step away from a monster was announced aloud ("I couldn't get away from the monster!") every ten seconds. The sibling test above is the control:
    /// an ordinary failed job IS spoken about within about ten seconds of ending.
    /// </summary>
    [Test]
    public async Task AFailedReflexJob_IsNotAnnouncedToHisFriends()
    {
        var log = new ConcurrentQueue<string>();
        var client = new MinecraftClient(GameProfile.Offline("VertexQuiet"));
        using var cts = new CancellationTokenSource(Slow);
        await client.ConnectAsync("127.0.0.1", Port, cts.Token);
        var root = Path.Combine(Path.GetTempPath(), $"vertex-tests-honest-{Guid.NewGuid():N}");
        _folders.Add(root);
        await using var brain = new VertexBrain(client, null, _ai, new CharacterStore(Path.Combine(root, "characters")), Character.BuiltIn[0], _model);
        await using var _c = client;
        await using var friend = new MinecraftClient(GameProfile.Offline("FriendQuiet"));
        await friend.ConnectAsync("127.0.0.1", Port, cts.Token);
        var heard = new ConcurrentQueue<ChatMessage>();
        friend.ChatReceived += heard.Enqueue;
        brain.Log += m => { log.Enqueue(m); TestContext.Out.WriteLine(m); };
        brain.Start();
        brain.Reflexes.Enabled = false; // only the job started here

        brain.Tasks.StartReflex("stepping away from monsters", _ => Task.FromResult((false, "I could not get away from the monsters.")));
        await Wait.UntilAsync(() => log.Any(l => l.StartsWith("task ended:")), Slow, () => "no task end; log: " + string.Join(" | ", log.TakeLast(6)));
        await Task.Delay(TimeSpan.FromSeconds(25));
        var said = string.Join(" ", heard.Where(m => m.SenderId == client.Profile.Id).Select(m => m.Content));
        Assert.That(said, Is.Empty, "a reflex's failure is logged, not announced: " + said);
    }
}
