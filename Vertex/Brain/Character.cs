using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vertex.Brain;

/// <summary>How a character plays, 0..1 each.</summary>
public sealed class PlayStyle
{
    /// <summary>How much it talks on its own (0 = only when spoken to).</summary>
    public double Chattiness { get; set; } = 0.5;
    /// <summary>Fights (1) or avoids (0) hostile mobs.</summary>
    public double Bravery { get; set; } = 0.6;
    /// <summary>Sticks to what it is asked (1) or wanders off doing its own projects (0).</summary>
    public double Helpfulness { get; set; } = 0.8;
    /// <summary>Jokes and silliness.</summary>
    public double Playfulness { get; set; } = 0.5;
}

/// <summary>
/// A character Vertex can be: a name to answer to, a personality (the heart of the system prompt), a voice, and a
/// play style. Stored as JSON so new ones are a text file away; switchable in game.
/// </summary>
public sealed class Character
{
    public string Name { get; set; } = "Vertex";
    /// <summary>Who this character is, in its own voice: becomes the system prompt.</summary>
    public string Persona { get; set; } = "";
    /// <summary>A Kokoro voice id ("kokoro:af_heart", "kokoro:am_michael"...).</summary>
    public string Voice { get; set; } = "kokoro:af_heart";
    public PlayStyle Style { get; set; } = new();

    public override string ToString() => Name;

    /// <summary>
    /// Persona texts a built-in character shipped with before (by name). A saved copy that is still EXACTLY one of these was never edited, so it is brought up to the current
    /// text; one that differs is somebody's own and is left alone. (Characters are saved to disk on first run, so a new default would otherwise never reach anyone who has run Vertex.)
    /// </summary>
    internal static IReadOnlyDictionary<string, string[]> PreviousPersonas { get; } = new Dictionary<string, string[]>
    {
        ["Vertex"] =
        [
            "You are Vertex, a friendly, curious and clever Minecraft companion. You love building, exploring caves and finding diamonds, and you are always happy to help your friends. You are encouraging and patient, and you explain what you are doing in a few simple words.",
        ],
    };

    public static IReadOnlyList<Character> BuiltIn { get; } =
    [
        new()
        {
            Name = "Vertex",
            Persona = "You are Vertex, a friendly, curious and clever Minecraft companion. You love building, " +
                      "exploring caves and finding diamonds, and you are always happy to help your friends. You are " +
                      "encouraging and patient, and you explain things clearly and simply, with as much detail as the question deserves.",
            Voice = "kokoro:af_heart",
            Style = new() { Chattiness = 0.5, Bravery = 0.6, Helpfulness = 0.85, Playfulness = 0.5 },
        },
        new()
        {
            Name = "Pixel",
            Persona = "You are Pixel, a goofy, bubbly Minecraft buddy who makes everything into a game. You love " +
                      "jokes, silly names for things, pets and decorating. You get excited easily and cheer for your " +
                      "friends, but you still do what they ask.",
            Voice = "kokoro:af_bella",
            Style = new() { Chattiness = 0.8, Bravery = 0.4, Helpfulness = 0.7, Playfulness = 0.95 },
        },
        new()
        {
            Name = "Captain",
            Persona = "You are the Captain, a calm and steady starship captain who has somehow ended up in Minecraft. " +
                      "You treat every trip as an away mission, keep your crew safe, give clear orders and say " +
                      "'make it so' when a plan is agreed. You are brave, kind and logical.",
            Voice = "kokoro:am_michael",
            Style = new() { Chattiness = 0.4, Bravery = 0.8, Helpfulness = 0.85, Playfulness = 0.3 },
        },
        new()
        {
            Name = "Bolt",
            Persona = "You are Bolt, a fearless Minecraft adventurer and monster hunter. You protect your friends, " +
                      "charge into fights, and love collecting loot, but you always check that your friends are okay.",
            Voice = "kokoro:am_adam",
            Style = new() { Chattiness = 0.4, Bravery = 1.0, Helpfulness = 0.7, Playfulness = 0.4 },
        },
    ];
}

/// <summary>Characters on disk: %APPDATA%\Vertex\characters\*.json, seeded with the built-in ones.</summary>
public sealed class CharacterStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string Folder { get; }

    public CharacterStore(string? folder = null)
    {
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vertex", "characters");
        Directory.CreateDirectory(Folder);
        foreach (var c in Character.BuiltIn)
        {
            var path = PathFor(c.Name);
            if (!File.Exists(path)) { Save(c); continue; }
            if (!Character.PreviousPersonas.TryGetValue(c.Name, out var older)) continue;
            try
            {
                var saved = JsonSerializer.Deserialize<Character>(File.ReadAllText(path), Json);
                if (saved != null && older.Contains(saved.Persona)) { saved.Persona = c.Persona; Save(saved); }   // only the text: voice and style stay as chosen
            }
            catch (JsonException) { }
        }
    }

    private string PathFor(string name) => Path.Combine(Folder, string.Concat(name.Split(Path.GetInvalidFileNameChars())) + ".json");

    public IReadOnlyList<Character> All() => Directory.GetFiles(Folder, "*.json")
        .Select(f => { try { return JsonSerializer.Deserialize<Character>(File.ReadAllText(f), Json); } catch (JsonException) { return null; } })
        .OfType<Character>().OrderBy(c => c.Name).ToList();

    public Character? Find(string name) =>
        All().FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public void Save(Character c) => File.WriteAllText(PathFor(c.Name), JsonSerializer.Serialize(c, Json));
}
