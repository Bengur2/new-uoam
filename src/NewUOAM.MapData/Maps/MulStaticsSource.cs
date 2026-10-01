namespace NewUOAM.MapData.Maps;

/// <summary>
/// Reads static items out of the classic staidxN.mul (per-block index) + staticsN.mul (item
/// records) pair.
///
/// staidxN.mul: one 12-byte record per block, in the same blockIndex order as the land data
/// (blockIndex = blockX * heightBlocks + blockY) - int32 offset into staticsN.mul (or -1 if this
/// block has no statics), int32 byte length, int32 unused. Cross-checked against real client
/// files before trusting this: staidx0.mul's file size / 12 exactly matches the facet's own
/// measured block count for both C:\Moria (393216 = 768x512) and the DP client
/// (458752 = 896x512) - same block-count agreement already established for the land data.
///
/// staticsN.mul: at that offset, length/7 fixed 7-byte records - ushort tileId, byte x, byte y
/// (both 0-7, offset within the block), sbyte z, short hue.
/// </summary>
public sealed class MulStaticsSource : IDisposable
{
    private const int IndexRecordSize = 12;
    private const int ItemRecordSize = 7;

    private readonly FileStream _idxStream;
    private readonly FileStream _dataStream;
    private readonly object _lock = new();

    public long BlockCount { get; }

    public MulStaticsSource(string staidxPath, string staticsPath)
    {
        _idxStream = new FileStream(staidxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _dataStream = new FileStream(staticsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        BlockCount = _idxStream.Length / IndexRecordSize;
    }

    public IReadOnlyList<StaticItem> GetBlockStatics(long blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= BlockCount) return Array.Empty<StaticItem>();

        lock (_lock)
        {
            _idxStream.Seek(blockIndex * IndexRecordSize, SeekOrigin.Begin);
            Span<byte> idxRec = stackalloc byte[IndexRecordSize];
            if (_idxStream.Read(idxRec) < IndexRecordSize) return Array.Empty<StaticItem>();

            int lookup = ReadInt32LE(idxRec);
            int length = ReadInt32LE(idxRec[4..]);
            if (lookup < 0 || length <= 0) return Array.Empty<StaticItem>();

            int count = length / ItemRecordSize;
            if (count <= 0) return Array.Empty<StaticItem>();

            _dataStream.Seek(lookup, SeekOrigin.Begin);
            byte[] raw = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = _dataStream.Read(raw, read, length - read);
                if (n == 0) break; // truncated file - return what we managed to read
                read += n;
            }
            count = read / ItemRecordSize;

            var result = new StaticItem[count];
            for (int i = 0; i < count; i++)
            {
                int o = i * ItemRecordSize;
                ushort tileId = (ushort)(raw[o] | (raw[o + 1] << 8));
                byte x = raw[o + 2];
                byte y = raw[o + 3];
                sbyte z = unchecked((sbyte)raw[o + 4]);
                short hue = (short)(raw[o + 5] | (raw[o + 6] << 8));
                result[i] = new StaticItem(tileId, x, y, z, hue);
            }
            return result;
        }
    }

    private static int ReadInt32LE(ReadOnlySpan<byte> b) => b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);

    public void Dispose()
    {
        _idxStream.Dispose();
        _dataStream.Dispose();
    }
}
