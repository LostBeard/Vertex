namespace SpawnDev.Minecraft.Physics;

/// <summary>
/// Vanilla's Mth trig: sine/cosine come from a 65536-entry float table, not Math.Sin. Movement uses them, so a
/// client that wants to land exactly where vanilla would must use the same table.
/// </summary>
public static class Mth
{
    private static readonly float[] SinTable = BuildTable();

    private static float[] BuildTable()
    {
        var t = new float[65536];
        for (var i = 0; i < t.Length; i++) t[i] = (float)Math.Sin(i * Math.PI * 2.0 / 65536.0);
        return t;
    }

    public static float Sin(float radians) => SinTable[(int)(radians * 10430.378f) & 65535];
    public static float Cos(float radians) => SinTable[(int)(radians * 10430.378f + 16384.0f) & 65535];

    public const float DegToRad = (float)Math.PI / 180f;

    /// <summary>Vanilla Mth.equal: |a - b| &lt; 1e-5.</summary>
    public static bool Equal(double a, double b) => Math.Abs(b - a) < 9.999999747378752E-6;

    public static float WrapDegrees(float degrees)
    {
        var f = degrees % 360f;
        if (f >= 180f) f -= 360f;
        if (f < -180f) f += 360f;
        return f;
    }
}
