using NewUOAM.MapData.Uop;
using NewUOAM.MapData.Util;

namespace NewUOAM.MapData.Maps;

/// <summary>
/// Reads land blocks out of a map{facet}LegacyMUL.uop package. Entries are looked up by the
/// name-hash the original packer used ("build/map{facet}legacymul/{index:D8}.dat") rather than
/// by physical file position, because physical layout order does not always match logical block
/// order (patch entries can be appended out of place - confirmed empirically against real
/// client files, see tools/UopProbe).
/// </summary>
public sealed class UopMapBlockSource : IMapBlockSource
{
    private const int BlockRecordSize = MulMapBlockSource.BlockRecordSize;
    private const int MaxCachedGroups = 64; // ~50 MB of decompressed cache at 4096 blocks/group

    private readonly UopFile _uop;
    private readonly int _facet;
    private readonly int _groupBlocks;
    private readonly Dictionary<int, UopEntry> _groupIndexToEntry = new();
    private readonly LruCache<int, byte[]> _decompressedCache = new(MaxCachedGroups);
    private readonly object _readLock = new(); // UopFile.ReadEntryData seeks a shared FileStream - not safe to call concurrently

    public long BlockCount { get; }

    public UopMapBlockSource(string uopPath, int facet)
    {
        _uop = UopFile.Open(uopPath);
        _facet = facet;

        _groupBlocks = _uop.EntriesInFileOrder
            .GroupBy(e => e.DecompressedSize)
            .OrderByDescending(g => g.Count())
            .First().Key / BlockRecordSize;

        if (_groupBlocks <= 0)
            throw new InvalidDataException($"Could not determine block-group size for {uopPath}.");

        // Probe sequential logical indices until we hit a run of misses - that gives us both
        // the index->entry map and the total block count (the last group may be short).
        int index = 0;
        int consecutiveMisses = 0;
        long lastGroupBlockCount = 0;
        while (consecutiveMisses < 4)
        {
            string name = $"build/map{_facet}legacymul/{index:D8}.dat";
            if (_uop.TryGetEntry(name, out var entry))
            {
                _groupIndexToEntry[index] = entry;
                lastGroupBlockCount = entry.DecompressedSize / BlockRecordSize;
                consecutiveMisses = 0;
            }
            else
            {
                consecutiveMisses++;
            }
            index++;
        }

        int highestIndex = _groupIndexToEntry.Count == 0 ? -1 : _groupIndexToEntry.Keys.Max();
        BlockCount = highestIndex < 0 ? 0 : (long)highestIndex * _groupBlocks + lastGroupBlockCount;
    }

    public void ReadBlockRaw(long blockIndex, Span<byte> destination)
    {
        if (destination.Length < BlockRecordSize)
            throw new ArgumentException($"Destination must be at least {BlockRecordSize} bytes.");

        int groupIndex = (int)(blockIndex / _groupBlocks);
        int withinGroupOffset = (int)(blockIndex % _groupBlocks) * BlockRecordSize;

        byte[] group = GetOrDecompressGroup(groupIndex);

        if (withinGroupOffset + BlockRecordSize > group.Length)
        {
            // Short trailing group (e.g. a partial patch entry) - beyond its data is empty land.
            destination[..BlockRecordSize].Clear();
            return;
        }

        group.AsSpan(withinGroupOffset, BlockRecordSize).CopyTo(destination);
    }

    private byte[] GetOrDecompressGroup(int groupIndex)
    {
        if (_decompressedCache.TryGet(groupIndex, out var cached))
            return cached;

        if (!_groupIndexToEntry.TryGetValue(groupIndex, out var entry))
            return new byte[_groupBlocks * BlockRecordSize]; // unmapped group -> empty land (not cached)

        lock (_readLock)
        {
            // Re-check: another thread may have decompressed this group while we waited for the lock.
            if (_decompressedCache.TryGet(groupIndex, out var raced))
                return raced;

            byte[] data = _uop.ReadEntryData(entry);
            _decompressedCache.Set(groupIndex, data);
            return data;
        }
    }

    public void Dispose() => _uop.Dispose();
}
