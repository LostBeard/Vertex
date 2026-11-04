namespace Vertex.Brain;

/// <summary>
/// Does the number on his screen actually hurt? A high reading is only a number until it burns him: he believes heat is dangerous from the moment it hurts him, and stops believing it after a stretch of high readings with no hurt. TJ turned heat damage off on
/// AubsCraft on 2026-11-04 with the temperature still showing (46 to 56 in hot biomes), and going by the number alone he would have gone on fleeing land that does him no harm. Learned from his own body, so it holds when a season or a server changes it.
/// </summary>
public sealed class HeatBelief
{
    /// <summary>How long he must read too hot without being burned to stop believing it. Heat burns every 3 seconds when it does.</summary>
    public static readonly TimeSpan Unharmed = TimeSpan.FromSeconds(20);

    private DateTime _lastBurn = DateTime.MinValue;
    private DateTime? _hotSince;

    /// <summary>Does heat hurt, as far as he has found? Not until it has.</summary>
    public bool Hurts { get; private set; }

    /// <summary>The heat hurt him. Returns true when this is news.</summary>
    public bool NoteBurn(DateTime now)
    {
        _lastBurn = now;
        var news = !Hurts;
        Hurts = true;
        return news;
    }

    /// <summary>What he reads now. Returns true when he has just stopped believing heat hurts.</summary>
    public bool NoteReading(double degrees, double hotAt, DateTime now)
    {
        if (degrees < hotAt) { _hotSince = null; return false; }
        _hotSince ??= now;
        if (!Hurts || now - _hotSince < Unharmed || now - _lastBurn < Unharmed) return false;
        Hurts = false;
        return true;
    }
}
