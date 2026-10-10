namespace Vertex.Tests.Live;

public static class Wait
{
    /// <summary>Polls a condition (the client updates on its receive loop) until true or the timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> describe)
    {
        var until = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("timed out waiting: " + describe());
            await Task.Delay(25);
        }
    }
}
