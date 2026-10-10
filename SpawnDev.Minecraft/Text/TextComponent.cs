using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SpawnDev.Minecraft.Nbt;

namespace SpawnDev.Minecraft.Text;

/// <summary>
/// Turns a chat component (network NBT form) into the plain text a player would read, using the game's own en_us
/// translations (extracted from the vanilla server jar by tools/DataGen).
/// </summary>
public static class TextComponent
{
    private static readonly Lazy<Dictionary<string, string>> English = new(LoadEnglish);

    /// <summary>The en_us translation for a key, or null when the game has none.</summary>
    public static string? Translate(string key) => English.Value.GetValueOrDefault(key);

    public static string ToPlainText(NbtTag? component)
    {
        if (component == null) return "";
        var sb = new StringBuilder();
        Append(sb, component, 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, NbtTag tag, int depth)
    {
        if (depth > 64) return; // vanilla caps component nesting too
        switch (tag)
        {
            case NbtString s:
                sb.Append(s.Value);
                return;
            case NbtList list:
                foreach (var item in list.Items) Append(sb, item, depth + 1);
                return;
            case NbtCompound c:
                // A heterogeneous NBT list wraps each element as {"": value}.
                if (c.Entries.Count == 1 && c[""] is NbtTag wrapped)
                {
                    Append(sb, wrapped, depth + 1);
                    return;
                }
                if (c.GetString("text") is string text) sb.Append(text);
                else if (c.GetString("translate") is string key) AppendTranslated(sb, c, key, depth);
                else if (c.GetCompound("score") is NbtCompound score) sb.Append(score.GetString("value") ?? "");
                else if (c.GetString("selector") is string selector) sb.Append(selector);
                else if (c.GetString("keybind") is string keybind) sb.Append(keybind);
                if (c.GetList("extra") is NbtList extra)
                    foreach (var item in extra.Items) Append(sb, item, depth + 1);
                return;
            default:
                // Numbers and booleans can appear as literal arguments.
                if (tag.AsNumber is double d) sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
        }
    }

    private static void AppendTranslated(StringBuilder sb, NbtCompound c, string key, int depth)
    {
        var format = Translate(key) ?? c.GetString("fallback") ?? key;
        var args = c.GetList("with")?.Items ?? [];
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            var ch = format[i];
            if (ch != '%' || i + 1 >= format.Length)
            {
                sb.Append(ch);
                continue;
            }
            var j = i + 1;
            if (format[j] == '%') { sb.Append('%'); i = j; continue; }
            // %s or %<n>$s (1-based positional)
            var index = next;
            var k = j;
            while (k < format.Length && char.IsAsciiDigit(format[k])) k++;
            if (k > j && k + 1 < format.Length && format[k] == '$' && format[k + 1] == 's')
            {
                index = int.Parse(format.AsSpan(j, k - j)) - 1;
                i = k + 1;
            }
            else if (format[j] == 's')
            {
                next++;
                i = j;
            }
            else
            {
                sb.Append(ch);
                continue;
            }
            if (index >= 0 && index < args.Count) Append(sb, args[index], depth + 1);
        }
    }

    private static Dictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(TextComponent).Assembly.GetManifestResourceStream("SpawnDev.Minecraft.Data.en_us.json.br")
            ?? throw new InvalidOperationException("embedded en_us.json.br missing (run tools/DataGen)");
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(brotli) ?? [];
    }
}
