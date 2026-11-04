using Vertex.Brain;

namespace Vertex.Tests;

/// <summary>
/// Learning from what happens (TJ, 2026-11-03: "help him learn to learn"). The recorder turns a stream of one-second moments into lessons in his own words, from facts it saw: a death, and a big loss of
/// health he got through. The first sequence is what happened live at 18:04: full health, three phantoms close, he steps away from them, and in about 70 seconds he is dead.
/// </summary>
public class ExperienceLogTests
{
    private static readonly DateTime T0 = new(2026, 11, 3, 18, 3, 0, DateTimeKind.Utc);
    private const long Night = 15000;
    private static readonly IReadOnlyList<(string, double)> Phantoms = [("minecraft:phantom", 4.0), ("minecraft:phantom", 6.0), ("minecraft:phantom", 9.5)];

    private static Moment At(int second, float health, string? task = "stepping away from monsters", IReadOnlyList<(string, double)>? mobs = null, long dayTime = Night, bool weapon = true, bool food = true)
        => new(T0.AddSeconds(second), health, 20, task, dayTime, "clear", "-52 63 1361", mobs ?? Phantoms, weapon, food);

    [Test]
    public void ADeath_IsALesson_WithWhatKilledHim_WhenItWas_WhatHeWasDoing_AndThatHeHadNotDugIn()
    {
        var log = new ExperienceLog();
        var lessons = new List<Skills.Lesson>();
        for (var s = 0; s < 10; s++) lessons.AddRange(log.Observe(At(s, 20)));
        foreach (var (s, h) in new[] { (10, 16f), (11, 12f), (12, 7f), (13, 2f) }) lessons.AddRange(log.Observe(At(s, h)));
        Assert.That(lessons, Is.Empty, "nothing before he is dead: a loss that is still going on is not yet a loss he got through");
        lessons.Clear();
        lessons.AddRange(log.Observe(At(14, 0), "HereticSpawn was slain by Phantom"));

        var death = lessons.Single();
        TestContext.Out.WriteLine(death.Text);
        Assert.That(death.Key, Is.EqualTo("death"));
        Assert.That(death.Worked, Is.False);
        Assert.That(death.Text, Does.Contain("HereticSpawn was slain by Phantom").And.Contain("night").And.Contain("3 phantoms").And.Contain("stepping away from monsters")
            .And.Contain("I had not taken cover").And.Contain("I had a weapon"));
        Assert.That(death.Text, Does.Contain("from 10 hearts to none"));
        Assert.That(death.Tags, Does.Contain("death").And.Contain("night").And.Contain("no-cover").And.Contain("death:phantom"));
    }

    [Test]
    public void ABigLoss_HeGotThrough_IsALesson_Once()
    {
        var log = new ExperienceLog();
        var lessons = new List<Skills.Lesson>();
        for (var s = 0; s < 5; s++) lessons.AddRange(log.Observe(At(s, 20)));
        foreach (var (s, h) in new[] { (5, 14f), (6, 8f), (7, 6f) }) lessons.AddRange(log.Observe(At(s, h, "taking cover underground")));
        for (var s = 8; s < 40; s++) lessons.AddRange(log.Observe(At(s, 6, "taking cover underground")));
        var loss = lessons.Single();
        TestContext.Out.WriteLine(loss.Text);
        Assert.That(loss.Key, Is.EqualTo("big-loss"));
        Assert.That(loss.Worked, Is.True, "he lived");
        Assert.That(loss.Text, Does.Contain("I lost 7 hearts").And.Contain("got through it").And.Contain("I had dug in"));
        Assert.That(loss.Tags, Does.Contain("big-loss").And.Contain("cover").And.Contain("big-loss:phantom"));
    }

    /// <summary>Live 2026-11-03: "I lost 9.5 hearts in a few seconds ... Nothing hostile was near", in daylight, again and again: falls he accepted when a path failed. The lesson now says so.</summary>
    [Test]
    public void ABigLossWithNothingNear_AfterAFall_SaysHowFarHeFell()
    {
        var log = new ExperienceLog();
        var lessons = new List<Skills.Lesson>();
        for (var s = 0; s < 5; s++) lessons.AddRange(log.Observe(At(s, 20, "collecting wood", [], dayTime: 6000) with { Y = 70 }));
        lessons.AddRange(log.Observe(At(5, 20, "collecting wood", [], dayTime: 6000) with { Y = 52 }));
        foreach (var s in new[] { 6, 7, 8, 9, 10, 11, 12 }) lessons.AddRange(log.Observe(At(s, 2, "collecting wood", [], dayTime: 6000) with { Y = 52 }));
        var loss = lessons.Single();
        TestContext.Out.WriteLine(loss.Text);
        Assert.That(loss.Text, Does.Contain("I fell about 18 blocks").And.Contain("Nothing hostile was near"));
    }

    /// <summary>"HereticSpawn burned to death" says nothing about the ground. The blocks round him in the last seconds are in the lesson.</summary>
    [Test]
    public void ABurnedToDeath_SaysWhatWasRoundHim()
    {
        var log = new ExperienceLog();
        for (var s = 0; s < 8; s++) log.Observe(At(s, 20, "collecting wood", [], dayTime: 6000) with { Hazard = s >= 5 ? "lava 1 blocks away" : null });
        var death = log.Observe(At(8, 0, "collecting wood", [], dayTime: 6000) with { Hazard = "I was standing in lava" }, "HereticSpawn burned to death").Single();
        TestContext.Out.WriteLine(death.Text);
        Assert.That(death.Text, Does.Contain("burned to death").And.Contain("Round me: lava 1 blocks away; I was standing in lava."));
    }

    [Test]
    public void AnEmptyFoodBar_IsInTheLesson()
    {
        var log = new ExperienceLog();
        for (var s = 0; s < 4; s++) log.Observe(At(s, 4, null, [], dayTime: 6000, food: false) with { Food = 2 });
        var death = log.Observe(At(4, 0, null, [], dayTime: 6000, food: false) with { Food = 0 }, "HereticSpawn starved to death").Single();
        Assert.That(death.Text, Does.Contain("starved to death").And.Contain("My food bar was 0 of 10: I was starving").And.Contain("I had no food"));
    }

    [Test]
    public void ADeathIsOneLesson_HoweverLongHeStaysDead_AndTheNextDeathAnother()
    {
        var log = new ExperienceLog();
        var n = 0;
        for (var s = 0; s < 3; s++) n += log.Observe(At(s, 20, mobs: [])).Count;
        for (var s = 3; s < 8; s++) n += log.Observe(At(s, 0, mobs: [])).Count;
        Assert.That(n, Is.EqualTo(1), "five seconds on the death screen are one death");
        for (var s = 8; s < 12; s++) n += log.Observe(At(s, 20, mobs: [])).Count;
        n += log.Observe(At(12, 0, mobs: [])).Count;
        Assert.That(n, Is.EqualTo(2), "back in the world and dead again: a second one");
    }

    [Test]
    public void ADeathWithNothingHostileNear_SaysSo_AndTheDaytimeAndTheLackOfAWeaponAreInIt()
    {
        var log = new ExperienceLog();
        for (var s = 0; s < 5; s++) log.Observe(At(s, 20, null, [], dayTime: 6000, weapon: false, food: false));
        var death = log.Observe(At(5, 0, null, [], dayTime: 6000, weapon: false, food: false), "HereticSpawn fell from a high place").Single();
        Assert.That(death.Text, Does.Contain("fell from a high place").And.Contain("It was day").And.Contain("Nothing hostile was near").And.Contain("not doing anything in particular")
            .And.Contain("I had no weapon").And.Contain("I had no food"));
        Assert.That(death.Tags, Does.Contain("day"));
    }

    [TestCase(0, 10)]
    [TestCase(1, 12)]
    [TestCase(2, 14)]
    [TestCase(3, 16)]
    [TestCase(9, 16)]
    [TestCase(-1, 10)]
    public void TheHealthHeTakesCoverAt_RisesWithEachDeathWithoutCover_UpToEightHearts(int deaths, int expected) => Assert.That(ExperienceLog.CoverBelow(deaths), Is.EqualTo(expected));
}
