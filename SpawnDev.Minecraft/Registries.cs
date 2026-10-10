using SpawnDev.Minecraft.Nbt;

namespace SpawnDev.Minecraft;

/// <summary>One registry entry sent during configuration. Value is null when the server relied on a known pack.</summary>
public sealed record RegistryEntry(string Key, NbtTag? Value);

/// <summary>
/// The synchronized registries the server sent during configuration (dimension types, chat types, biomes,
/// damage types...). Network ids are the entry indexes, in the order the server sent them.
/// </summary>
public sealed class Registries
{
    private readonly Dictionary<string, List<RegistryEntry>> _registries = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Names => _registries.Keys;

    internal void Set(string registry, List<RegistryEntry> entries) => _registries[registry] = entries;

    internal void Clear() => _registries.Clear();

    public IReadOnlyList<RegistryEntry> this[string registry] =>
        _registries.TryGetValue(registry, out var list) ? list : [];

    public RegistryEntry? Get(string registry, int networkId)
    {
        var list = this[registry];
        return networkId >= 0 && networkId < list.Count ? list[networkId] : null;
    }

    public RegistryEntry? Get(string registry, string key)
    {
        foreach (var e in this[registry]) if (e.Key == key) return e;
        return null;
    }
}
