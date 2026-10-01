using NewUOAM.MapData.Util;

namespace NewUOAM.MapData.Maps;

/// <summary>
/// A single playable facet/map (Felucca, Trammel, ...): resolves land tiles from whichever
/// underlying file format the client install has (.mul preferred when present, else .uop).
/// </summary>
public sealed class MapFacet : IDisposable
{
    private readonly IMapBlockSource _source;

    // Raw-block cache: GetLandTile() is called per-pixel by the renderer (once per screen pixel,
    // for both the north-up and the rotated/isometric projection), and every pixel within the
    // same 8x8-tile block would otherwise re-read/re-seek the same 196 bytes. A tiny LRU cache
    // turns that into a dictionary hit after the first pixel touches a given block - this matters
    // most for MulMapBlockSource, where an uncached read is a file seek per tile.
    private const int MaxCachedBlocks = 8192; // ~1.6 MB of raw block bytes
    private readonly LruCache<long, byte[]> _blockCache = new(MaxCachedBlocks);

    private readonly MulStaticsSource? _statics;
    private readonly LruCache<long, IReadOnlyList<StaticItem>> _staticsCache = new(MaxCachedBlocks);

    public bool HasStatics => _statics is not null;

    public int Index { get; }
    public string Name { get; }
    public int WidthBlocks { get; }
    public int HeightBlocks { get; }
    public int WidthTiles => WidthBlocks * 8;
    public int HeightTiles => HeightBlocks * 8;

    /// <summary>Every file this facet's data actually came from (whichever of map*.mul/
    /// map*LegacyMUL.uop was used, plus staidx*.mul/statics*.mul if present) - for callers that
    /// want to cache derived data (e.g. <see cref="Colors.FacetColorMap"/>) and need to know what
    /// to check for staleness.</summary>
    public IReadOnlyList<string> SourceFilePaths { get; }

    private MapFacet(int index, string name, int widthBlocks, int heightBlocks, IMapBlockSource source,
        MulStaticsSource? statics, IReadOnlyList<string> sourceFilePaths)
    {
        Index = index;
        Name = name;
        WidthBlocks = widthBlocks;
        HeightBlocks = heightBlocks;
        _source = source;
        _statics = statics;
        SourceFilePaths = sourceFilePaths;
    }

    public static MapFacet Open(string clientDir, int facetIndex)
    {
        string? mulPath = FindFile(clientDir, $"map{facetIndex}.mul");
        string? uopPath = FindFile(clientDir, $"map{facetIndex}LegacyMUL.uop");

        IMapBlockSource source;
        long blockCount;

        if (mulPath is not null)
        {
            var mul = new MulMapBlockSource(mulPath);
            source = mul;
            blockCount = mul.BlockCount;
        }
        else if (uopPath is not null)
        {
            var uop = new UopMapBlockSource(uopPath, facetIndex);
            source = uop;
            blockCount = uop.BlockCount;
        }
        else
        {
            throw new FileNotFoundException($"No map{facetIndex}.mul or map{facetIndex}LegacyMUL.uop found in '{clientDir}'.");
        }

        var known = FacetInfo.Known.GetValueOrDefault(facetIndex);
        string name = known?.Name ?? $"Facet {facetIndex}";
        (int widthBlocks, int heightBlocks) = ResolveDimensions(blockCount, known);

        var sourcePaths = new List<string> { mulPath ?? uopPath! };

        MulStaticsSource? statics = null;
        string? staidxPath = FindFile(clientDir, $"staidx{facetIndex}.mul");
        string? staticsPath = FindFile(clientDir, $"statics{facetIndex}.mul");
        if (staidxPath is not null && staticsPath is not null)
        {
            try
            {
                statics = new MulStaticsSource(staidxPath, staticsPath);
                sourcePaths.Add(staidxPath);
                sourcePaths.Add(staticsPath);
            }
            catch (IOException) { statics = null; } // statics are a nice-to-have, not worth failing the whole facet over
        }

        return new MapFacet(facetIndex, name, widthBlocks, heightBlocks, source, statics, sourcePaths);
    }

    /// <summary>
    /// Works out (widthBlocks, heightBlocks) from a measured total block count. Never trusts a
    /// facet's own "known standard" width blindly - a shard can legitimately give facet slot N
    /// the dimensions normally associated with a different stock facet (observed in practice: a
    /// custom Felucca sized exactly like stock Trammel, 896x512 instead of 768x512). Blindly
    /// keeping the "expected" width and truncating height via integer division silently produces
    /// a wrong stride, which corrupts the blockIndex -> (blockX, blockY) mapping and renders as
    /// diagonal/striped garbage (every row samples the wrong block) - so every candidate width is
    /// checked against the actual measured block count instead.
    /// </summary>
    private static (int Width, int Height) ResolveDimensions(long blockCount, FacetInfo? known)
    {
        // A trailing block-group patch entry can add a handful of stray blocks (observed: +1)
        // without changing the real grid shape, so allow a small remainder instead of requiring
        // exact division.
        const long maxSlack = 64;

        var candidateWidths = new List<int>();
        if (known is not null) candidateWidths.Add(known.WidthBlocks);
        candidateWidths.AddRange(FacetInfo.Known.Values.Select(f => f.WidthBlocks));

        foreach (int width in candidateWidths.Distinct())
        {
            if (width <= 0 || width > blockCount) continue;
            long remainder = blockCount % width;
            if (remainder <= maxSlack || width - remainder <= maxSlack)
            {
                long height = blockCount / width;
                if (height > 0) return (width, (int)height);
            }
        }

        // Try common block-grid heights too, in case a custom width is paired with a standard height.
        foreach (int height in new[] { 512, 4096, 256, 200, 181 })
        {
            long remainder = blockCount % height;
            if (remainder <= maxSlack || height - remainder <= maxSlack)
            {
                long width = blockCount / height;
                if (width > 0) return ((int)width, height);
            }
        }

        // Genuinely unknown/custom shape - square-ish guess so GetLandTile at least doesn't crash.
        int guess = (int)Math.Max(1, Math.Sqrt(blockCount));
        return (guess, (int)Math.Max(1, blockCount / guess));
    }

    private static string? FindFile(string dir, string fileName)
    {
        string direct = Path.Combine(dir, fileName);
        if (File.Exists(direct)) return direct;

        // Client folders are sometimes case-inconsistent; do a cheap case-insensitive scan.
        try
        {
            return Directory.EnumerateFiles(dir)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public LandTile GetLandTile(int x, int y)
    {
        if (x < 0 || y < 0 || x >= WidthTiles || y >= HeightTiles)
            return default;

        int blockX = x / 8, blockY = y / 8;
        int cellX = x % 8, cellY = y % 8;
        long blockIndex = (long)blockX * HeightBlocks + blockY;

        byte[] raw = GetCachedBlock(blockIndex);

        int cellIndex = cellY * 8 + cellX; // standard legacy cell ordering within a block
        int cellOffset = 4 + cellIndex * 3; // skip 4-byte block header
        ushort tileId = (ushort)(raw[cellOffset] | (raw[cellOffset + 1] << 8));
        sbyte z = unchecked((sbyte)raw[cellOffset + 2]);
        return new LandTile(tileId, z);
    }

    private byte[] GetCachedBlock(long blockIndex) => _blockCache.GetOrAdd(blockIndex, idx =>
    {
        byte[] raw = new byte[MulMapBlockSource.BlockRecordSize];
        _source.ReadBlockRaw(idx, raw);
        return raw;
    });

    /// <summary>All statics standing on the given tile, bottom to top (by Z). Empty if this
    /// facet has no statics data, or the tile is empty/out of range.</summary>
    public IReadOnlyList<StaticItem> GetStaticsAt(int x, int y)
    {
        if (_statics is null || x < 0 || y < 0 || x >= WidthTiles || y >= HeightTiles)
            return Array.Empty<StaticItem>();

        int blockX = x / 8, blockY = y / 8;
        int cellX = x % 8, cellY = y % 8;
        long blockIndex = (long)blockX * HeightBlocks + blockY;

        var blockStatics = _staticsCache.GetOrAdd(blockIndex, idx => _statics.GetBlockStatics(idx));
        if (blockStatics.Count == 0) return Array.Empty<StaticItem>();

        List<StaticItem>? matches = null;
        foreach (var s in blockStatics)
        {
            if (s.X == cellX && s.Y == cellY)
                (matches ??= new List<StaticItem>()).Add(s);
        }
        return (IReadOnlyList<StaticItem>?)matches ?? Array.Empty<StaticItem>();
    }

    /// <summary>The static drawn on this tile instead of the land in the given view, if any (only
    /// one thing shows per tile, like the in-game radar map). See <see cref="StaticsView"/> for
    /// the rules. <paramref name="landZ"/> is the tile's land Z.</summary>
    public bool TryGetDrawnStatic(int x, int y, sbyte landZ, StaticsView view, out StaticItem drawn)
    {
        drawn = default;
        if (view == StaticsView.Hidden) return false;

        bool found = false;
        foreach (var s in GetStaticsAt(x, y))
        {
            bool eligible = view == StaticsView.Normal ? s.Z >= landZ : s.Z <= landZ;
            // ">=": on equal Z the later one in statics.mul wins, as in old UOAM.
            if (eligible && (!found || s.Z >= drawn.Z))
            {
                drawn = s;
                found = true;
            }
        }
        return found;
    }

    public void Dispose()
    {
        _source.Dispose();
        _statics?.Dispose();
    }
}
