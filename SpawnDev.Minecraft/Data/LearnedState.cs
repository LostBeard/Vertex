namespace SpawnDev.Minecraft.Data;

/// <summary>
/// What Vertex learned by experience about one block state of a modded server that it has no data for (by raw
/// state id, which means the same block only while the server's block list stays the same). Each fact is learned
/// the way a player would: touching it, right-clicking it, placing it, breaking it.
/// </summary>
public sealed record LearnedState
{
    /// <summary>Its collision box, felt by touch (MinecraftClient.MeasureBlockAsync). Null: not measured yet.</summary>
    public Box[]? Shape { get; init; }

    /// <summary>It lets you in and then holds you (a cobweb): pathing avoids it.</summary>
    public bool Sticky { get; init; }

    /// <summary>
    /// The block's name, from placing an item and seeing what it made, or breaking it and seeing what dropped.
    /// With a name, the server's own tags apply (a mod's doors are in minecraft:wooden_doors).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Right-clicking it turns it into this state (a door opening). Null: not tried, or nothing changed.</summary>
    public int? UseTurnsInto { get; init; }

    /// <summary>Right-clicked and nothing changed (not a door, lever or anything else that toggles).</summary>
    public bool UseDoesNothing { get; init; }

    /// <summary>
    /// A door or gate: one of a pair of states that right-clicking switches between, one blocking the way and the
    /// other letting you through. Pathing walks through it (opening it first when closed), along AlongX.
    /// </summary>
    public bool Doorway { get; init; }

    /// <summary>For a doorway: this is the state that lets you through.</summary>
    public bool Open { get; init; }

    /// <summary>For a doorway: you go through it moving along x (its closed plate is thin in x), else along z.</summary>
    public bool AlongX { get; init; }

    /// <summary>
    /// Game ticks the server took to break it (MinecraftClient.TimeBreakAsync), with DigTool in the hand (null:
    /// bare hand or an item that is no tool). Its hardness follows from the tool's speed.
    /// </summary>
    public int? DigTicks { get; init; }
    public string? DigTool { get; init; }

    /// <summary>
    /// This plus what `other` learned, where facts come in groups learned together (shape and sticky; what
    /// right-clicking does; the dig) and a group `other` has replaces this one's.
    /// </summary>
    public LearnedState With(LearnedState other)
    {
        var r = this;
        if (other.Shape != null) r = r with { Shape = other.Shape, Sticky = other.Sticky };
        if (other.Name != null) r = r with { Name = other.Name };
        if (other.UseTurnsInto != null || other.UseDoesNothing)
            r = r with { UseTurnsInto = other.UseTurnsInto, UseDoesNothing = other.UseDoesNothing, Doorway = other.Doorway, Open = other.Open, AlongX = other.AlongX };
        if (other.DigTicks != null) r = r with { DigTicks = other.DigTicks, DigTool = other.DigTool };
        return r;
    }
}
