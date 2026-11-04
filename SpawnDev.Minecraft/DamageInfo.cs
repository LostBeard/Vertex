namespace SpawnDev.Minecraft;

/// <summary>
/// What just hurt us, from the server's damage event: the vanilla damage type without its namespace ("arrow", "on_fire", "mob_attack", "drown", "starve"...) and the entities behind it.
/// </summary>
/// <param name="Type">The damage type's registry name, without "minecraft:".</param>
/// <param name="CauseEntityId">Who is behind it (the skeleton that shot the arrow), or -1.</param>
/// <param name="DirectEntityId">What actually touched us (the arrow), or -1.</param>
/// <param name="From">Where it came from, when the server said.</param>
public sealed record DamageInfo(string Type, int CauseEntityId, int DirectEntityId, (double X, double Y, double Z)? From);
