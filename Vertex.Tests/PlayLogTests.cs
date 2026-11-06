using NUnit.Framework;
using Vertex.Host;

namespace Vertex.Tests;

/// <summary>The log left running while nobody watches: it keeps what he did, never what people say, and it cannot fill the disk.</summary>
[TestFixture]
public class PlayLogTests
{
    private string _folder = "";

    [SetUp] public void SetUp() => _folder = Path.Combine(Path.GetTempPath(), "playlog-" + Guid.NewGuid().ToString("N"));
    [TearDown] public void TearDown() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    private string All() => string.Concat(Directory.GetFiles(_folder).Select(File.ReadAllText));

    [Test]
    public void WhatHeDidIsKept_WhatPeopleSayIsNot()
    {
        var log = new PlayLog(_folder) { FreeBytes = () => long.MaxValue };
        var now = new DateTime(2026, 11, 6, 8, 0, 0);
        log.Line(now, LineKind.System, "danger: running away");
        log.Line(now, LineKind.Error, "job crashed");
        log.Line(now, LineKind.Heard, "[Game update, not a friend talking: you just died.");
        log.Line(now, LineKind.Heard, "PRIVATE-HEARD");
        log.Line(now, LineKind.Said, "PRIVATE-SAID");
        log.Line(now, LineKind.Chat, "PRIVATE-CHAT");
        var all = All();
        Assert.That(all, Does.Contain("danger: running away").And.Contain("job crashed").And.Contain("you just died"));
        Assert.That(all, Does.Not.Contain("PRIVATE"));
    }

    [Test]
    public void PastTheCap_TheOldestDaysGo()
    {
        var log = new PlayLog(_folder) { FreeBytes = () => long.MaxValue, MaxBytes = 100_000 };
        var line = new string('x', 1000);
        // 3 days, more than the cap; the check is every 200 lines.
        for (var day = 1; day <= 3; day++)
            for (var i = 0; i < 250; i++)
                log.Line(new DateTime(2026, 11, day), LineKind.System, line);
        var names = Directory.GetFiles(_folder).Select(Path.GetFileName).ToList();
        Assert.That(names, Does.Not.Contain("play-2026-11-01.log"), "the oldest went");
        Assert.That(names, Does.Contain("play-2026-11-03.log"), "the newest stayed");
    }

    [Test]
    public void WithLittleLeftOnTheDrive_NothingIsWritten()
    {
        var log = new PlayLog(_folder) { FreeBytes = () => 1024 };
        log.Line(DateTime.Now, LineKind.System, "a line");
        log.Status(DateTime.Now, new { health = 20 });
        Assert.That(Directory.Exists(_folder), Is.False);
    }

    [Test]
    public void StatusSaysWhetherHeIsInTheGame()
    {
        var log = new PlayLog(_folder) { FreeBytes = () => long.MaxValue };
        log.Status(DateTime.Now, new { health = 20 });
        log.Status(DateTime.Now, null);
        var all = All();
        Assert.That(all, Does.Contain("\"inGame\":true").And.Contain("\"health\":20").And.Contain("\"inGame\":false"));
    }
}
