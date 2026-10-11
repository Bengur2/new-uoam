namespace NewUOAM.MapData.Colors;

/// <summary>
/// Reads hues.mul, the client's text/item color palette: groups of a 4-byte header + 8 entries,
/// each entry 32 × 16-bit 5-5-5 colors (a dark → light ramp), table start/end (2 × 2 bytes) and a
/// 20-byte name = 88 bytes. Hue number h (1-based, as sent in packets) is entry (h-1)%8 of group
/// (h-1)/8.
///
/// Why the app needs it: a hue number means whatever the shard's hues.mul says. The map chat's
/// turquoise 0x05BA is a proper ramp in Dark Paradise's file, but in Endor Revived's it's junk
/// (29 of 32 colors are (0,0,8)), so the chat text came out black there (found 2026-10-11).
/// <see cref="Resolve"/> keeps a wanted hue when the client's ramp for it is usable and otherwise
/// picks the usable hue closest to the intended color.
/// </summary>
public sealed class HueTable
{
    private const int EntrySize = 88;
    private const int GroupSize = 4 + 8 * EntrySize;
    /// <summary>The ramp color compared against the intended color: upper-middle of the ramp,
    /// roughly what text rendering shows.</summary>
    private const int SampleIndex = 24;

    private readonly ushort[][] _ramps;

    private HueTable(ushort[][] ramps) => _ramps = ramps;

    public int Count => _ramps.Length;

    public static HueTable Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var ramps = new List<ushort[]>();
        for (int group = 0; (group + 1) * GroupSize <= bytes.Length; group++)
        {
            for (int entry = 0; entry < 8; entry++)
            {
                int offset = group * GroupSize + 4 + entry * EntrySize;
                var ramp = new ushort[32];
                for (int i = 0; i < 32; i++)
                    ramp[i] = (ushort)(bytes[offset + i * 2] | (bytes[offset + i * 2 + 1] << 8));
                ramps.Add(ramp);
            }
        }
        return new HueTable(ramps.ToArray());
    }

    /// <summary>The intended color of <paramref name="hue"/> in this palette (its ramp sample),
    /// or null for a hue outside the file.</summary>
    public Rgb? GetColor(ushort hue) =>
        hue >= 1 && hue <= _ramps.Length ? ToRgb(_ramps[hue - 1][SampleIndex]) : null;

    /// <summary>A real dark → light ramp: brightness never drops noticeably from one color to the
    /// next and the lightest color is visible. Junk entries (mostly black with a few stray colors)
    /// fail this. Tested on DP's and Endor's files: 2284 / 1950 of 3000 hues pass.</summary>
    public bool IsUsable(ushort hue)
    {
        if (hue < 1 || hue > _ramps.Length) return false;
        ushort[] ramp = _ramps[hue - 1];
        for (int i = 1; i < ramp.Length; i++)
            if (Brightness(ramp[i]) + 30 < Brightness(ramp[i - 1])) return false;
        return Brightness(ramp[^1]) > 150;
    }

    /// <summary><paramref name="wanted"/> when it's usable in this palette and still looks like
    /// <paramref name="intended"/>; otherwise the usable hue whose color is closest to it.</summary>
    public ushort Resolve(ushort wanted, Rgb intended)
    {
        if (IsUsable(wanted) && Distance(ToRgb(_ramps[wanted - 1][SampleIndex]), intended) < 60 * 60) return wanted;
        ushort best = wanted;
        int bestDistance = int.MaxValue;
        for (int i = 0; i < _ramps.Length; i++)
        {
            ushort hue = (ushort)(i + 1);
            if (!IsUsable(hue)) continue;
            int distance = Distance(ToRgb(_ramps[i][SampleIndex]), intended);
            if (distance < bestDistance) { bestDistance = distance; best = hue; }
        }
        return best;
    }

    private static int Distance(Rgb a, Rgb b) =>
        (a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B);

    private static int Brightness(ushort c)
    {
        Rgb rgb = ToRgb(c);
        return rgb.R + rgb.G + rgb.B;
    }

    private static Rgb ToRgb(ushort v) =>
        new((byte)(((v >> 10) & 0x1F) * 255 / 31), (byte)(((v >> 5) & 0x1F) * 255 / 31), (byte)((v & 0x1F) * 255 / 31));
}
