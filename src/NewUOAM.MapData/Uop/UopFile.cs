using System.IO.Compression;

namespace NewUOAM.MapData.Uop;

public sealed class UopEntry
{
    public long Offset { get; init; }
    public int HeaderLength { get; init; }
    public int CompressedSize { get; init; }
    public int DecompressedSize { get; init; }
    public ulong Hash { get; init; }
    public int Crc { get; init; }
    public short CompressionMethod { get; init; }
}

/// <summary>
/// Reader for the generic Mythic ".uop" package container (magic "MYP\0").
/// This only parses the container structure (header -> chained block tables -> entries).
/// It does not know anything about what the entries mean - callers look entries up by
/// <see cref="UopHash.HashFileName"/> of the logical file name.
/// </summary>
public sealed class UopFile : IDisposable
{
    private const uint Magic = 0x50594D; // "MYP\0" little-endian

    private readonly FileStream _stream;
    private readonly Dictionary<ulong, UopEntry> _entriesByHash = new();
    private readonly List<UopEntry> _entriesInFileOrder = new();

    public IReadOnlyDictionary<ulong, UopEntry> EntriesByHash => _entriesByHash;

    /// <summary>
    /// Entries in the order they appear in the block-table chain (i.e. the order the original
    /// packer wrote them in). For packages such as map*LegacyMUL.uop this order matches the
    /// sequential logical index (0, 1, 2, ...) that the file names were generated from.
    /// </summary>
    public IReadOnlyList<UopEntry> EntriesInFileOrder => _entriesInFileOrder;
    public int Version { get; private set; }
    public uint FormatTimestamp { get; private set; }

    private UopFile(FileStream stream) => _stream = stream;

    public static UopFile Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var uop = new UopFile(fs);
        uop.ReadHeaderAndEntries();
        return uop;
    }

    private void ReadHeaderAndEntries()
    {
        using var br = new BinaryReader(_stream, System.Text.Encoding.ASCII, leaveOpen: true);

        uint magic = br.ReadUInt32();
        if (magic != Magic)
            throw new InvalidDataException($"Not a .uop file (bad magic 0x{magic:X}).");

        Version = br.ReadInt32();
        FormatTimestamp = br.ReadUInt32();
        long nextBlockOffset = br.ReadInt64();
        uint blockCapacity = br.ReadUInt32(); // entries per block (informational)
        uint fileCount = br.ReadUInt32();
        _ = blockCapacity;
        _ = fileCount;

        while (nextBlockOffset != 0)
        {
            _stream.Seek(nextBlockOffset, SeekOrigin.Begin);
            int entriesInBlock = br.ReadInt32();
            long nextBlock = br.ReadInt64();

            for (int i = 0; i < entriesInBlock; i++)
            {
                long offset = br.ReadInt64();
                int headerLength = br.ReadInt32();
                int compressedSize = br.ReadInt32();
                int decompressedSize = br.ReadInt32();
                ulong hash = br.ReadUInt64();
                int crc = br.ReadInt32();
                short compression = br.ReadInt16();

                if (offset == 0)
                    continue; // empty slot

                var e = new UopEntry
                {
                    Offset = offset,
                    HeaderLength = headerLength,
                    CompressedSize = compressedSize,
                    DecompressedSize = decompressedSize,
                    Hash = hash,
                    Crc = crc,
                    CompressionMethod = compression,
                };
                _entriesByHash[hash] = e;
                _entriesInFileOrder.Add(e);
            }

            nextBlockOffset = nextBlock;
        }
    }

    public bool TryGetEntry(string logicalFileName, out UopEntry entry) =>
        _entriesByHash.TryGetValue(UopHash.HashFileName(logicalFileName), out entry!);

    public byte[] ReadEntryData(UopEntry entry)
    {
        _stream.Seek(entry.Offset + entry.HeaderLength, SeekOrigin.Begin);
        byte[] raw = new byte[entry.CompressedSize];
        int readTotal = 0;
        while (readTotal < raw.Length)
        {
            int n = _stream.Read(raw, readTotal, raw.Length - readTotal);
            if (n == 0) throw new EndOfStreamException();
            readTotal += n;
        }

        if (entry.CompressionMethod == 0)
            return raw;

        if (entry.CompressionMethod == 1)
        {
            using var input = new MemoryStream(raw);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(entry.DecompressedSize > 0 ? entry.DecompressedSize : raw.Length * 4);
            zlib.CopyTo(output);
            return output.ToArray();
        }

        throw new NotSupportedException($"Unknown UOP compression method {entry.CompressionMethod}.");
    }

    public void Dispose() => _stream.Dispose();
}
