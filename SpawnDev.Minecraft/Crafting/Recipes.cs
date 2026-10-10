using System.Text.Json.Nodes;
using SpawnDev.Minecraft.Data;

namespace SpawnDev.Minecraft.Crafting;

/// <summary>Which items fit one recipe slot.</summary>
public sealed class Ingredient(IReadOnlySet<string> items)
{
    public IReadOnlySet<string> Items { get; } = items;
    public bool Matches(string itemName) => Items.Contains(itemName);
    public override string ToString() => Items.Count == 1 ? Items.First() : $"one of {Items.Count}";
}

/// <summary>
/// A crafting recipe from the game's data pack: a grid of ingredients (null = empty) and what it makes. Shapeless
/// recipes are laid out left-to-right, top-to-bottom (any arrangement works for them).
/// </summary>
public sealed class CraftingRecipe
{
    public required string Id { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Row-major cells, Width x Height.</summary>
    public required Ingredient?[] Cells { get; init; }
    public required string Result { get; init; }
    public required int ResultCount { get; init; }
    public bool Shapeless { get; init; }

    /// <summary>Fits in the 2x2 inventory grid (else it needs a crafting table).</summary>
    public bool FitsInInventory => Width <= 2 && Height <= 2;

    public override string ToString() => $"{Id}: {ResultCount}x {Result} ({Width}x{Height})";
}

/// <summary>A smelting-type recipe (furnace, blast furnace, smoker, campfire).</summary>
public sealed record CookingRecipe(string Id, string Type, Ingredient Input, string Result, int CookingTicks);

/// <summary>The crafting and smelting recipes of one game version, from the vanilla data pack (tools/DataGen).</summary>
public sealed class RecipeBook
{
    private static readonly Lazy<RecipeBook> _vanilla = new(() => new RecipeBook(GameData.Vanilla));
    public static RecipeBook Vanilla => _vanilla.Value;

    private readonly Dictionary<string, List<CraftingRecipe>> _byResult = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CookingRecipe>> _cookingByResult = new(StringComparer.Ordinal);

    public RecipeBook(GameData data)
    {
        Ingredient Parse(JsonNode node)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            void Add(string s)
            {
                if (s.StartsWith('#')) set.UnionWith(data.ItemTag(s));
                else set.Add(GameData.Normalize(s));
            }
            if (node is JsonArray a) foreach (var x in a) Add(x!.GetValue<string>());
            else if (node is JsonValue v) Add(v.GetValue<string>());
            else if (node is JsonObject o && o["item"] is JsonNode item) Add(item.GetValue<string>()); // older format
            return new Ingredient(set);
        }

        foreach (var (id, node) in data.Recipes)
        {
            var type = node!["type"]?.GetValue<string>();
            var result = node["result"];
            if (type is "minecraft:crafting_shaped" && result != null)
            {
                var pattern = node["pattern"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();
                var key = node["key"]!.AsObject().ToDictionary(k => k.Key[0], k => Parse(k.Value!));
                var width = pattern.Max(r => r.Length);
                var cells = new Ingredient?[width * pattern.Length];
                for (var y = 0; y < pattern.Length; y++)
                for (var x = 0; x < pattern[y].Length; x++)
                    cells[y * width + x] = pattern[y][x] == ' ' ? null : key[pattern[y][x]];
                Add(new CraftingRecipe
                {
                    Id = id, Width = width, Height = pattern.Length, Cells = cells,
                    Result = GameData.Normalize(result["id"]!.GetValue<string>()), ResultCount = result["count"]?.GetValue<int>() ?? 1,
                });
            }
            else if (type is "minecraft:crafting_shapeless" && result != null)
            {
                var ingredients = node["ingredients"]!.AsArray().Select(i => Parse(i!)).ToArray();
                var width = ingredients.Length <= 4 ? 2 : 3;
                var height = (ingredients.Length + width - 1) / width;
                if (ingredients.Length == 1) { width = 1; height = 1; }
                var cells = new Ingredient?[width * height];
                Array.Copy(ingredients, cells, ingredients.Length);
                Add(new CraftingRecipe
                {
                    Id = id, Width = width, Height = height, Cells = cells, Shapeless = true,
                    Result = GameData.Normalize(result["id"]!.GetValue<string>()), ResultCount = result["count"]?.GetValue<int>() ?? 1,
                });
            }
            else if (type is "minecraft:smelting" or "minecraft:blasting" or "minecraft:smoking" or "minecraft:campfire_cooking" && result != null)
            {
                var r = new CookingRecipe(id, type!, Parse(node["ingredient"]!), GameData.Normalize(result["id"]!.GetValue<string>()), node["cookingtime"]?.GetValue<int>() ?? 200);
                (_cookingByResult.TryGetValue(r.Result, out var l) ? l : _cookingByResult[r.Result] = []).Add(r);
            }
        }
    }

    private void Add(CraftingRecipe r) => (_byResult.TryGetValue(r.Result, out var l) ? l : _byResult[r.Result] = []).Add(r);

    public IReadOnlyList<CraftingRecipe> RecipesFor(string item) =>
        _byResult.TryGetValue(GameData.Normalize(item), out var l) ? l : [];

    public IReadOnlyList<CookingRecipe> CookingRecipesFor(string item) =>
        _cookingByResult.TryGetValue(GameData.Normalize(item), out var l) ? l : [];
}
