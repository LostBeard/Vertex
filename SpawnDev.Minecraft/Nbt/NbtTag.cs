namespace SpawnDev.Minecraft.Nbt;

public enum NbtType : byte
{
    End = 0, Byte = 1, Short = 2, Int = 3, Long = 4, Float = 5, Double = 6, ByteArray = 7, String = 8,
    List = 9, Compound = 10, IntArray = 11, LongArray = 12,
}

/// <summary>A Named Binary Tag value (network form: tags inside compounds are named, the root is not).</summary>
public abstract class NbtTag
{
    public abstract NbtType Type { get; }

    /// <summary>The value as a double when it is any numeric tag, else null.</summary>
    public virtual double? AsNumber => null;
}

public sealed class NbtByte(sbyte value) : NbtTag
{
    public sbyte Value { get; } = value;
    public override NbtType Type => NbtType.Byte;
    public override double? AsNumber => Value;
    public override string ToString() => Value + "b";
}

public sealed class NbtShort(short value) : NbtTag
{
    public short Value { get; } = value;
    public override NbtType Type => NbtType.Short;
    public override double? AsNumber => Value;
    public override string ToString() => Value + "s";
}

public sealed class NbtInt(int value) : NbtTag
{
    public int Value { get; } = value;
    public override NbtType Type => NbtType.Int;
    public override double? AsNumber => Value;
    public override string ToString() => Value.ToString();
}

public sealed class NbtLong(long value) : NbtTag
{
    public long Value { get; } = value;
    public override NbtType Type => NbtType.Long;
    public override double? AsNumber => Value;
    public override string ToString() => Value + "L";
}

public sealed class NbtFloat(float value) : NbtTag
{
    public float Value { get; } = value;
    public override NbtType Type => NbtType.Float;
    public override double? AsNumber => Value;
    public override string ToString() => Value + "f";
}

public sealed class NbtDouble(double value) : NbtTag
{
    public double Value { get; } = value;
    public override NbtType Type => NbtType.Double;
    public override double? AsNumber => Value;
    public override string ToString() => Value + "d";
}

public sealed class NbtByteArray(byte[] value) : NbtTag
{
    public byte[] Value { get; } = value;
    public override NbtType Type => NbtType.ByteArray;
}

public sealed class NbtString(string value) : NbtTag
{
    public string Value { get; } = value;
    public override NbtType Type => NbtType.String;
    public override string ToString() => Value;
}

public sealed class NbtList(NbtType elementType, List<NbtTag> items) : NbtTag
{
    public NbtType ElementType { get; } = elementType;
    public List<NbtTag> Items { get; } = items;
    public override NbtType Type => NbtType.List;
}

public sealed class NbtCompound(Dictionary<string, NbtTag> entries) : NbtTag
{
    public Dictionary<string, NbtTag> Entries { get; } = entries;
    public override NbtType Type => NbtType.Compound;

    public NbtTag? this[string key] => Entries.GetValueOrDefault(key);

    public string? GetString(string key) => (Entries.GetValueOrDefault(key) as NbtString)?.Value;
    public double? GetNumber(string key) => Entries.GetValueOrDefault(key)?.AsNumber;
    public int? GetInt(string key) => GetNumber(key) is double d ? (int)d : null;
    public bool? GetBool(string key) => GetNumber(key) is double d ? d != 0 : null;
    public NbtCompound? GetCompound(string key) => Entries.GetValueOrDefault(key) as NbtCompound;
    public NbtList? GetList(string key) => Entries.GetValueOrDefault(key) as NbtList;
}

public sealed class NbtIntArray(int[] value) : NbtTag
{
    public int[] Value { get; } = value;
    public override NbtType Type => NbtType.IntArray;
}

public sealed class NbtLongArray(long[] value) : NbtTag
{
    public long[] Value { get; } = value;
    public override NbtType Type => NbtType.LongArray;
}
