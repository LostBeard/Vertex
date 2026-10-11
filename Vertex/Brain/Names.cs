namespace Vertex.Brain;

public static class Names
{
    /// <summary>A registry name without its namespace ("minecraft:oak_log" -> "oak_log", a mod's too), for speech and matching.</summary>
    public static string Bare(this string name) => name[(name.IndexOf(':') + 1)..];
}
