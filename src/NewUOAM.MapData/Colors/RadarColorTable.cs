namespace NewUOAM.MapData.Colors;

public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// Reads radarcol.mul: a flat array of 16-bit 5-5-5 RGB colors, one per graphic/tile id - the
/// same table the in-game radar map and the old UOAM used for its "authentic" colored map.
/// </summary>
public sealed class RadarColorTable
{
    private readonly ushort[] _colors;

    private RadarColorTable(ushort[] colors) => _colors = colors;

    public static RadarColorTable Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int count = bytes.Length / 2;
        var colors = new ushort[count];
        for (int i = 0; i < count; i++)
            colors[i] = (ushort)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
        return new RadarColorTable(colors);
    }

    public Rgb GetColor(ushort tileId)
    {
        if (tileId >= _colors.Length) return default;
        ushort v = _colors[tileId];
        int r5 = (v >> 10) & 0x1F;
        int g5 = (v >> 5) & 0x1F;
        int b5 = v & 0x1F;
        return new Rgb((byte)(r5 * 255 / 31), (byte)(g5 * 255 / 31), (byte)(b5 * 255 / 31));
    }
}
