using System.IO.Compression;
using System.Text.Json;

namespace SpawnDev.Minecraft.Protocol;

/// <summary>
/// Reads values described by minecraft-data's ProtoDef type definitions (protocol.json "types", MIT, embedded by
/// tools/DataGen). Used for the structures with dozens of variants that are not worth hand-coding one by one:
/// item stacks with their data components, entity metadata, particles. Values come back as
/// Dictionary&lt;string, object?&gt; (containers), List&lt;object?&gt; (arrays) and primitives / NbtTag / byte[].
/// Every live test that decodes inventories checks the parse lands exactly on the packet end, which is what
/// catches a wrong definition.
/// </summary>
public sealed class ProtoDef
{
    private static readonly Lazy<ProtoDef> _vanilla = new(() => Load("SpawnDev.Minecraft.Data.protocol-types.json.br"));
    public static ProtoDef Vanilla => _vanilla.Value;

    private readonly JsonDocument _doc;
    private readonly Dictionary<string, JsonElement> _types = new(StringComparer.Ordinal);

    private ProtoDef(JsonDocument doc)
    {
        _doc = doc;
        foreach (var t in doc.RootElement.GetProperty("types").EnumerateObject()) _types[t.Name] = t.Value;
    }

    public static ProtoDef Load(string resource)
    {
        using var stream = typeof(ProtoDef).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded {resource} missing (run tools/DataGen)");
        using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
        return new ProtoDef(JsonDocument.Parse(brotli));
    }

    public bool HasType(string name) => _types.ContainsKey(name);

    /// <summary>Reads one value of a named type (e.g. "Slot", "entityMetadata").</summary>
    public object? Read(ref PacketReader r, string typeName) => ReadNamed(ref r, typeName, null);

    private sealed class Scope(Dictionary<string, object?> fields, Scope? parent)
    {
        public Dictionary<string, object?> Fields { get; } = fields;
        public Scope? Parent { get; } = parent;
    }

    private object? ReadNamed(ref PacketReader r, string name, Scope? scope)
    {
        switch (name)
        {
            case "varint": case "optvarint": return r.ReadVarInt();
            case "varlong": return r.ReadVarLong();
            case "u8": return r.ReadByte();
            case "i8": return r.ReadSByte();
            case "u16": return r.ReadUShort();
            case "i16": return r.ReadShort();
            case "u32": return (uint)r.ReadInt();
            case "i32": return r.ReadInt();
            case "u64": return (ulong)r.ReadLong();
            case "i64": return r.ReadLong();
            case "f32": return r.ReadFloat();
            case "f64": return r.ReadDouble();
            case "bool": return r.ReadBool();
            case "UUID": return r.ReadUuid();
            case "void": return null;
            case "restBuffer": return r.ReadRest().ToArray();
            case "anonymousNbt": case "anonOptionalNbt": return r.ReadNbt();
            case "string": return r.ReadString();
        }
        if (!_types.TryGetValue(name, out var def)) throw new InvalidDataException($"unknown protocol type '{name}'");
        return ReadType(ref r, def, scope);
    }

    private object? ReadType(ref PacketReader r, JsonElement type, Scope? scope)
    {
        if (type.ValueKind == JsonValueKind.String) return ReadNamed(ref r, type.GetString()!, scope);
        var kind = type[0].GetString()!;
        var opts = type[1];
        switch (kind)
        {
            case "container":
            {
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                var inner = new Scope(fields, scope);
                foreach (var f in opts.EnumerateArray())
                {
                    var value = ReadType(ref r, f.GetProperty("type"), inner);
                    var anon = f.TryGetProperty("anon", out var a) && a.GetBoolean() || !f.TryGetProperty("name", out _);
                    if (anon)
                    {
                        if (value is Dictionary<string, object?> merged) foreach (var (k, v) in merged) fields[k] = v;
                    }
                    else fields[f.GetProperty("name").GetString()!] = value;
                }
                return fields;
            }
            case "switch":
            {
                var key = Resolve(opts.GetProperty("compareTo").GetString()!, scope);
                var keyText = key switch { bool b => b ? "true" : "false", null => "null", _ => key.ToString()! };
                if (opts.TryGetProperty("fields", out var cases) && cases.TryGetProperty(keyText, out var chosen))
                    return ReadType(ref r, chosen, scope);
                if (opts.TryGetProperty("default", out var fallback)) return ReadType(ref r, fallback, scope);
                return null;
            }
            case "option":
                return r.ReadBool() ? ReadType(ref r, opts, scope) : null;
            case "array":
            {
                int count;
                if (opts.TryGetProperty("countType", out var countType)) count = Convert.ToInt32(ReadType(ref r, countType, scope));
                else
                {
                    var c = opts.GetProperty("count");
                    count = c.ValueKind == JsonValueKind.Number ? c.GetInt32() : Convert.ToInt32(Resolve(c.GetString()!, scope));
                }
                if (count < 0 || count > r.Remaining) throw new InvalidDataException($"array count {count} with {r.Remaining} bytes left");
                var list = new List<object?>(count);
                var element = opts.GetProperty("type");
                for (var i = 0; i < count; i++) list.Add(ReadType(ref r, element, scope));
                return list;
            }
            case "pstring":
            {
                var count = Convert.ToInt32(ReadType(ref r, opts.GetProperty("countType"), scope));
                return System.Text.Encoding.UTF8.GetString(r.ReadBytes(count));
            }
            case "buffer":
            {
                var count = opts.TryGetProperty("countType", out var ct) ? Convert.ToInt32(ReadType(ref r, ct, scope)) : opts.GetProperty("count").GetInt32();
                return r.ReadBytes(count).ToArray();
            }
            case "mapper":
            {
                var raw = ReadType(ref r, opts.GetProperty("type"), scope);
                var text = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)!;
                return opts.GetProperty("mappings").TryGetProperty(text, out var mapped) ? mapped.GetString() : text;
            }
            case "bitfield":
            {
                var totalBits = opts.EnumerateArray().Sum(f => f.GetProperty("size").GetInt32());
                var bytes = r.ReadBytes(totalBits / 8);
                ulong value = 0;
                foreach (var b in bytes) value = value << 8 | b;
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                var shift = totalBits;
                foreach (var f in opts.EnumerateArray())
                {
                    var size = f.GetProperty("size").GetInt32();
                    shift -= size;
                    var v = (long)((value >> shift) & ((1UL << size) - 1));
                    if (f.GetProperty("signed").GetBoolean() && (v & (1L << (size - 1))) != 0) v -= 1L << size;
                    fields[f.GetProperty("name").GetString()!] = v;
                }
                return fields;
            }
            case "bitflags":
            {
                var raw = Convert.ToInt64(ReadType(ref r, opts.GetProperty("type"), scope));
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["_value"] = raw };
                var flags = opts.GetProperty("flags");
                if (flags.ValueKind == JsonValueKind.Array)
                {
                    var bit = 0;
                    foreach (var f in flags.EnumerateArray()) fields[f.GetString()!] = (raw >> bit++ & 1) != 0;
                }
                else foreach (var f in flags.EnumerateObject()) fields[f.Name] = (raw & f.Value.GetInt64()) != 0;
                return fields;
            }
            case "registryEntryHolder":
            {
                var id = r.ReadVarInt();
                if (id != 0) return new Dictionary<string, object?> { [opts.GetProperty("baseName").GetString()!] = id - 1 };
                var otherwise = opts.GetProperty("otherwise");
                return new Dictionary<string, object?> { [otherwise.GetProperty("name").GetString()!] = ReadType(ref r, otherwise.GetProperty("type"), scope) };
            }
            case "registryEntryHolderSet":
            {
                var n = r.ReadVarInt();
                if (n == 0)
                {
                    var b = opts.GetProperty("base");
                    return new Dictionary<string, object?> { [b.GetProperty("name").GetString()!] = ReadType(ref r, b.GetProperty("type"), scope) };
                }
                var otherwise = opts.GetProperty("otherwise");
                var ids = new List<object?>(n - 1);
                for (var i = 0; i < n - 1; i++) ids.Add(ReadType(ref r, otherwise.GetProperty("type"), scope));
                return new Dictionary<string, object?> { [otherwise.GetProperty("name").GetString()!] = ids };
            }
            case "topBitSetTerminatedArray":
            {
                // Each element's first byte carries "more follow" in its top bit (entity equipment).
                var list = new List<object?>();
                while (true)
                {
                    var more = (r.Rest[0] & 0x80) != 0;
                    var element = ReadType(ref r, opts.GetProperty("type"), scope);
                    if (element is Dictionary<string, object?> d && d.Count > 0)
                    {
                        var first = d.Keys.First();
                        if (d[first] is sbyte sb) d[first] = (sbyte)(sb & 0x7F);
                        else if (d[first] is byte ub) d[first] = (byte)(ub & 0x7F);
                    }
                    list.Add(element);
                    if (!more) return list;
                }
            }
            case "entityMetadataLoop":
            {
                var end = opts.GetProperty("endVal").GetInt32();
                var list = new List<object?>();
                while (r.Rest[0] != end) list.Add(ReadType(ref r, opts.GetProperty("type"), scope));
                r.ReadByte();
                return list;
            }
            default:
                throw new InvalidDataException($"unsupported protocol type kind '{kind}'");
        }
    }

    /// <summary>Resolves a compareTo/count path: "field", "../field", "../a/b" (nested container fields).</summary>
    private static object? Resolve(string path, Scope? scope)
    {
        var parts = path.Split('/');
        var s = scope;
        var i = 0;
        while (i < parts.Length && parts[i] == "..") { s = s?.Parent; i++; }
        if (path.StartsWith('$')) { parts = [path[1..]]; i = 0; }
        object? current = null;
        // The first name is looked up in this scope or the nearest enclosing one that has it.
        for (var look = s; look != null; look = look.Parent)
            if (look.Fields.TryGetValue(parts[i], out current)) break;
        for (i++; i < parts.Length; i++)
            current = current is Dictionary<string, object?> d ? d.GetValueOrDefault(parts[i]) : null;
        return current;
    }
}
