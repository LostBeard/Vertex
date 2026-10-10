namespace SpawnDev.Minecraft;

public enum ChatKind
{
    /// <summary>Server/system text: join/leave, death messages, command output, /say, plugin messages.</summary>
    System,
    /// <summary>Text shown above the hotbar.</summary>
    ActionBar,
    /// <summary>A player's chat message.</summary>
    Player,
    /// <summary>Chat sent without a player profile (e.g. /say from the console with a chat type).</summary>
    Disguised,
}

/// <summary>
/// A chat line. Text is the full line as a player would read it ("&lt;Aubs&gt; hi"); Content is just the message
/// body ("hi") and SenderName/SenderId say who sent it, when known.
/// </summary>
public sealed record ChatMessage(ChatKind Kind, string Text, string Content, Guid? SenderId = null, string? SenderName = null);
