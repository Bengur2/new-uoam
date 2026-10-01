using System.Text;

namespace NewUOAM.MapData.Uop;

/// <summary>
/// Jenkins "lookup3" hashlittle2 (public domain, Bob Jenkins - http://burtleburtle.net/bob/c/lookup3.c).
/// The .uop container format used by later Ultima Online clients does not store file names;
/// instead every entry is keyed by a 64-bit hash of the logical file name the client would have used
/// (e.g. "build/map0legacymul/00000000.dat"). This reproduces that hash so entries can be located
/// by name instead of position.
/// </summary>
public static class UopHash
{
    public static ulong HashFileName(string fileName)
    {
        byte[] data = Encoding.ASCII.GetBytes(fileName);
        return HashLittle2(data);
    }

    private static ulong HashLittle2(byte[] data)
    {
        int length = data.Length;
        uint a, b, c;
        a = b = c = 0xDEADBEEF + (uint)length;

        int k = 0;
        int remaining = length;

        while (remaining > 12)
        {
            a += data[k] | ((uint)data[k + 1] << 8) | ((uint)data[k + 2] << 16) | ((uint)data[k + 3] << 24);
            b += data[k + 4] | ((uint)data[k + 5] << 8) | ((uint)data[k + 6] << 16) | ((uint)data[k + 7] << 24);
            c += data[k + 8] | ((uint)data[k + 9] << 8) | ((uint)data[k + 10] << 16) | ((uint)data[k + 11] << 24);

            a -= c; a ^= Rot(c, 4); c += b;
            b -= a; b ^= Rot(a, 6); a += c;
            c -= b; c ^= Rot(b, 8); b += a;
            a -= c; a ^= Rot(c, 16); c += b;
            b -= a; b ^= Rot(a, 19); a += c;
            c -= b; c ^= Rot(b, 4); b += a;

            k += 12;
            remaining -= 12;
        }

        if (remaining > 0)
        {
            switch (remaining)
            {
                case 12: c += (uint)data[k + 11] << 24; goto case 11;
                case 11: c += (uint)data[k + 10] << 16; goto case 10;
                case 10: c += (uint)data[k + 9] << 8; goto case 9;
                case 9: c += data[k + 8]; goto case 8;
                case 8: b += (uint)data[k + 7] << 24; goto case 7;
                case 7: b += (uint)data[k + 6] << 16; goto case 6;
                case 6: b += (uint)data[k + 5] << 8; goto case 5;
                case 5: b += data[k + 4]; goto case 4;
                case 4: a += (uint)data[k + 3] << 24; goto case 3;
                case 3: a += (uint)data[k + 2] << 16; goto case 2;
                case 2: a += (uint)data[k + 1] << 8; goto case 1;
                case 1: a += data[k + 0]; break;
            }

            c ^= b; c -= Rot(b, 14);
            a ^= c; a -= Rot(c, 11);
            b ^= a; b -= Rot(a, 25);
            c ^= b; c -= Rot(b, 16);
            a ^= c; a -= Rot(c, 4);
            b ^= a; b -= Rot(a, 14);
            c ^= b; c -= Rot(b, 24);
        }

        // NOTE: the word order here (b high, c low) was determined empirically against real
        // client map*LegacyMUL.uop files - it matches the "build/map{facet}legacymul/{i:D8}.dat"
        // naming convention used by the .uop packer. See tools/UopProbe for the validation.
        return ((ulong)b << 32) | c;
    }

    private static uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));
}
