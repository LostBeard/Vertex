namespace SpawnDev.Minecraft;

/// <summary>A player in the server's player list (the tab list), including ones not in view.</summary>
public sealed class PlayerInfo(Guid id, string name)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public GameMode GameMode { get; internal set; }
    public bool Listed { get; internal set; }
    public int LatencyMs { get; internal set; }
    public string? DisplayName { get; internal set; }
    /// <summary>The chat session id the server accepted for this player (signed chat), if any.</summary>
    public Guid? ChatSessionId { get; internal set; }

    public override string ToString() => Name;
}
