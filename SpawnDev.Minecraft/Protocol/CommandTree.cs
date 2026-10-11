using System.Globalization;
using SpawnDev.Minecraft.Data;

namespace SpawnDev.Minecraft.Protocol;

/// <summary>
/// The server's command tree (the play "commands" packet): Brigadier nodes as the server declared them for this
/// player. Used to find which arguments of a command must be signed: with enforce-secure-profile (Paper's
/// default) the server refuses to run an unsigned command that has a signable argument (/msg, /me, /say,
/// /teammsg...) and answers with "invalid command signature" instead (ServerGamePacketListenerImpl
/// .performUnsignedChatCommand, read from the 1.21.5 server's bytecode).
/// </summary>
public sealed class CommandTree
{
    public enum NodeType { Root = 0, Literal = 1, Argument = 2 }

    public sealed class Node
    {
        public NodeType Type { get; init; }
        public string Name { get; init; } = "";
        /// <summary>Argument parser ("minecraft:message", "brigadier:string"...), null for root and literals.</summary>
        public string? Parser { get; init; }
        /// <summary>brigadier:string mode: 0 single word, 1 quotable phrase, 2 greedy phrase.</summary>
        public int StringMode { get; init; }
        public bool Executable { get; init; }
        public int[] Children { get; init; } = [];
        public int Redirect { get; init; } = -1;
    }

    public Node[] Nodes { get; }
    public int Root { get; }

    private CommandTree(Node[] nodes, int root)
    {
        Nodes = nodes;
        Root = root;
    }

    /// <summary>Top-level command names this player may use.</summary>
    public IEnumerable<string> CommandNames => Nodes[Root].Children.Select(c => Nodes[c].Name);

    /// <summary>Reads the commands packet. Throws InvalidDataException for a parser id the game data does not know.</summary>
    public static CommandTree Read(ref PacketReader r)
    {
        var nodes = new Node[r.ReadVarInt()];
        for (var i = 0; i < nodes.Length; i++)
        {
            var flags = r.ReadByte();
            var type = (NodeType)(flags & 3);
            var children = new int[r.ReadVarInt()];
            for (var c = 0; c < children.Length; c++) children[c] = r.ReadVarInt();
            var redirect = (flags & 0x08) != 0 ? r.ReadVarInt() : -1;
            string name = "";
            string? parser = null;
            var stringMode = 0;
            if (type is NodeType.Literal or NodeType.Argument) name = r.ReadString();
            if (type == NodeType.Argument)
            {
                var id = r.ReadVarInt();
                parser = GameData.Vanilla.RegistryName("minecraft:command_argument_type", id)
                    ?? throw new InvalidDataException($"unknown command argument type {id} on '{name}'");
                stringMode = SkipProperties(ref r, parser);
            }
            if ((flags & 0x10) != 0) r.ReadString(); // custom suggestions provider
            nodes[i] = new Node
            {
                Type = type,
                Name = name,
                Parser = parser,
                StringMode = stringMode,
                Executable = (flags & 0x04) != 0,
                Children = children,
                Redirect = redirect,
            };
        }
        var root = r.ReadVarInt();
        if ((uint)root >= (uint)nodes.Length) throw new InvalidDataException($"command root {root} of {nodes.Length} nodes");
        return new CommandTree(nodes, root);
    }

    /// <summary>
    /// Skips an argument type's properties (each ArgumentTypeInfo.serializeToNetwork in the 1.21.5 server);
    /// returns the brigadier:string mode.
    /// </summary>
    private static int SkipProperties(ref PacketReader r, string parser)
    {
        switch (parser)
        {
            case "brigadier:float":
            case "brigadier:integer":
            {
                var flags = r.ReadByte(); // ArgumentUtils.createNumberFlags: 1 = min present, 2 = max present
                if ((flags & 1) != 0) r.ReadInt();
                if ((flags & 2) != 0) r.ReadInt();
                return 0;
            }
            case "brigadier:double":
            case "brigadier:long":
            {
                var flags = r.ReadByte();
                if ((flags & 1) != 0) r.ReadLong();
                if ((flags & 2) != 0) r.ReadLong();
                return 0;
            }
            case "brigadier:string":
                return r.ReadVarInt();
            case "minecraft:entity":
            case "minecraft:score_holder":
                r.ReadByte();
                return 0;
            case "minecraft:time":
                r.ReadInt(); // minimum ticks
                return 0;
            case "minecraft:resource_or_tag":
            case "minecraft:resource_or_tag_key":
            case "minecraft:resource":
            case "minecraft:resource_key":
            case "minecraft:resource_selector":
                r.ReadString(); // registry
                return 0;
            default:
                return 0; // singleton argument types carry no properties
        }
    }

    /// <summary>
    /// The arguments of <paramref name="command"/> (without the '/') that must be signed, in parse order, as
    /// (argument name, the exact text it covers): what SignableCommand.of collects on the server. Empty when the
    /// command has none or does not parse against this tree (the server then has nothing to verify either).
    /// </summary>
    /// <remarks>
    /// SignableCommand.of follows child contexts only while their root differs from the dispatcher root, so it
    /// stops at a redirect back to the root: "/execute ... run say hi" has nothing to sign (the 1.21.5 server
    /// answers a signature for it with "Signed command mismatch ... expected []"), while /tell (redirect to the
    /// /msg node) still signs its message.
    /// </remarks>
    public List<(string Name, string Value)> SignableArguments(string command)
    {
        var path = new List<(int Node, int Start, int End, bool Collected)>();
        return Walk(Root, command, 0, true, path)
            ? path.Where(p => p.Collected && Nodes[p.Node].Parser == "minecraft:message")
                .Select(p => (Nodes[p.Node].Name, command[p.Start..p.End])).ToList()
            : [];
    }

    /// <summary>
    /// Brigadier's CommandDispatcher.parseNodes: a literal that matches the next word wins outright, otherwise each
    /// argument child is tried in order; a child is followed by end of input, or by a space and its children (or
    /// its redirect target's children). The first parse that consumes everything is the one the server runs.
    /// </summary>
    private bool Walk(int node, string input, int cursor, bool collected, List<(int Node, int Start, int End, bool Collected)> path)
    {
        foreach (var child in RelevantChildren(node, input, cursor))
        {
            var n = Nodes[child];
            var end = n.Type == NodeType.Literal ? cursor + n.Name.Length : ParseArgument(n, input, cursor);
            if (end < 0) continue;
            if (end < input.Length && input[end] != ' ') continue; // brigadier: expected a separator
            path.Add((child, cursor, end, collected));
            if (end == input.Length) return true;
            // A redirect continues after a single space; ordinary children need a space and something after it.
            if (n.Redirect >= 0
                    ? Walk(n.Redirect, input, end + 1, collected && n.Redirect != Root, path)
                    : end + 1 < input.Length && Walk(child, input, end + 1, collected, path))
                return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private IEnumerable<int> RelevantChildren(int node, string input, int cursor)
    {
        var children = Nodes[node].Children;
        var spaceAt = input.IndexOf(' ', cursor);
        var word = spaceAt < 0 ? input[cursor..] : input[cursor..spaceAt];
        foreach (var c in children)
            if (Nodes[c].Type == NodeType.Literal && Nodes[c].Name == word) return [c];
        return children.Where(c => Nodes[c].Type == NodeType.Argument);
    }

    /// <summary>Where an argument starting at <paramref name="start"/> ends, or -1 if it cannot parse there.</summary>
    private static int ParseArgument(Node n, string input, int start)
    {
        switch (n.Parser)
        {
            case "minecraft:message":
                return input.Length; // MessageArgument takes the rest of the command
            case "brigadier:string":
                return n.StringMode switch
                {
                    2 => input.Length,
                    1 when start < input.Length && input[start] is '"' or '\'' => QuotedEnd(input, start),
                    _ => NonEmpty(start, UnquotedEnd(input, start)),
                };
            case "brigadier:bool":
            {
                var end = UnquotedEnd(input, start);
                return input[start..end] is "true" or "false" ? end : -1;
            }
            case "brigadier:integer":
            case "brigadier:long":
            {
                var end = NumberEnd(input, start);
                return long.TryParse(input.AsSpan(start, end - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ? end : -1;
            }
            case "brigadier:float":
            case "brigadier:double":
            {
                var end = NumberEnd(input, start);
                return double.TryParse(input.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? end : -1;
            }
            case "minecraft:block_pos":
            case "minecraft:vec3":
                return Tokens(input, start, 3);
            case "minecraft:vec2":
            case "minecraft:column_pos":
            case "minecraft:rotation":
                return Tokens(input, start, 2);
            default:
                // Selectors (@e[type=cow, limit=1]), names, ids, item stacks with components, SNBT, JSON text:
                // one token, where spaces inside brackets or quotes do not end it.
                return NonEmpty(start, TokenEnd(input, start));
        }
    }

    private static int NonEmpty(int start, int end) => end > start ? end : -1;

    /// <summary>StringReader.isAllowedInUnquotedString: 0-9 A-Z a-z _ - . +</summary>
    private static int UnquotedEnd(string s, int i)
    {
        while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.' or '+')) i++;
        return i;
    }

    private static int NumberEnd(string s, int i)
    {
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] is '.' or '-')) i++;
        return i;
    }

    private static int QuotedEnd(string s, int i)
    {
        var quote = s[i++];
        while (i < s.Length)
        {
            if (s[i] == '\\') i += 2;
            else if (s[i++] == quote) return i;
        }
        return -1;
    }

    private static int TokenEnd(string s, int i)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c is '"' or '\'')
            {
                var end = QuotedEnd(s, i);
                if (end < 0) return s.Length;
                i = end;
                continue;
            }
            if (c is '[' or '{' or '(') depth++;
            else if (c is ']' or '}' or ')') depth = Math.Max(0, depth - 1);
            else if (c == ' ' && depth == 0) break;
            i++;
        }
        return i;
    }

    private static int Tokens(string s, int i, int count)
    {
        for (var t = 0; t < count; t++)
        {
            if (t > 0)
            {
                if (i >= s.Length || s[i] != ' ') return -1;
                i++;
            }
            var end = TokenEnd(s, i);
            if (end == i) return -1;
            i = end;
        }
        return i;
    }
}
