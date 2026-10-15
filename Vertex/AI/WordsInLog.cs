namespace Vertex.AI;

/// <summary>
/// The words of speech nobody addressed to Vertex do not go into the log unless asked for.
/// </summary>
/// <remarks>
/// The robot's microphone hears the whole room, and the dashboard that shows the log can be opened from any device on the network without a login. Live 2026-10-15 the log held a
/// household conversation about gift cards, word for word. The log still says who spoke, how long, how loud and what Vertex did about it; the words of what is said TO Vertex are logged
/// by the conversation itself (the "heard" and "said" lines), and the setting "Write everything the robot hears in the log" turns the rest back on for debugging recognition.
/// </remarks>
public static class WordsInLog
{
    /// <summary>The quoted words, or a placeholder when words are not being logged.</summary>
    public static string Quote(string text, bool logWords) => logWords ? $"\"{text}\"" : "(words not logged)";
}
