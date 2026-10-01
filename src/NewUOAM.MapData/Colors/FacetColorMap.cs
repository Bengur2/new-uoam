using NewUOAM.MapData.Maps;

namespace NewUOAM.MapData.Colors;

/// <summary>
/// A facet's tiles (in one <see cref="StaticsView"/>) decoded to color exactly once, at 1 pixel per tile (BGRA32), so that
/// panning/zooming/rotating the view is just sampling an in-memory array instead of re-decoding
/// raw map blocks + a radar-color lookup for every screen pixel on every repaint.
///
/// Memory cost is real (a 7168x4096 facet is ~112 MiB at 4 bytes/tile) but this is built once per
/// facet and reused for the rest of the session - the tradeoff the caller is choosing by building
/// this at all instead of calling <see cref="MapFacet.GetLandTile"/> directly.
/// </summary>
public sealed class FacetColorMap
{
    private readonly byte[] _bgra; // B,G,R,A per tile, row-major (y * Width + x)

    public int WidthTiles { get; }
    public int HeightTiles { get; }
    public StaticsView View { get; }

    private FacetColorMap(int widthTiles, int heightTiles, StaticsView view, byte[] bgra)
    {
        WidthTiles = widthTiles;
        HeightTiles = heightTiles;
        View = view;
        _bgra = bgra;
    }

    /// <summary>radarcol.mul is one flat table covering land tile ids (0x0000-0x3FFF) followed
    /// immediately by item/static graphic ids (0x4000 onward, item graphic 0 at index 0x4000) -
    /// confirmed by the table's own size (65536 entries = exactly land's 16384 + items' 49152).</summary>
    private const int StaticRadarColorOffset = 0x4000;

    public static FacetColorMap Build(MapFacet facet, RadarColorTable? radar, StaticsView view, CancellationToken ct = default)
    {
        int w = facet.WidthTiles, h = facet.HeightTiles;
        var data = new byte[(long)w * h * 4];

        for (int y = 0; y < h; y++)
        {
            if ((y & 63) == 0) ct.ThrowIfCancellationRequested();
            long rowStart = (long)y * w * 4;
            for (int x = 0; x < w; x++)
            {
                TileColor(facet, radar, view, x, y, out byte r, out byte g, out byte b);

                long i = rowStart + (long)x * 4;
                data[i + 0] = b;
                data[i + 1] = g;
                data[i + 2] = r;
                data[i + 3] = 255;
            }
        }

        return new FacetColorMap(w, h, view, data);
    }

    /// <summary>One tile's color in the given view - shared by <see cref="Build"/> and the app's
    /// on-demand fallback for a facet whose color map isn't ready yet, so both draw the same.</summary>
    public static void TileColor(MapFacet facet, RadarColorTable? radar, StaticsView view, int x, int y,
        out byte r, out byte g, out byte b)
    {
        var tile = facet.GetLandTile(x, y);

        // A static (building/tree/...) shown on this tile is drawn instead of the land, matching
        // the classic in-game radar map - which one (if any) depends on the view.
        if (radar is not null && facet.HasStatics && facet.TryGetDrawnStatic(x, y, tile.Z, view, out var top))
        {
            var c = radar.GetColor((ushort)(top.TileId + StaticRadarColorOffset));
            r = c.R; g = c.G; b = c.B;
        }
        else if (radar is not null)
        {
            var c = radar.GetColor(tile.TileId);
            r = c.R; g = c.G; b = c.B;
        }
        else
        {
            r = g = b = (byte)Math.Clamp(128 + tile.Z * 3, 0, 255);
        }
    }

    /// <summary>Rebuilds a FacetColorMap from raw bytes previously obtained via <see cref="Bgra"/>
    /// - used by <see cref="FacetColorMapDiskCache"/> to load a saved one back in.</summary>
    public static FacetColorMap FromRawBgra(int widthTiles, int heightTiles, StaticsView view, byte[] bgra) => new(widthTiles, heightTiles, view, bgra);

    /// <summary>Raw BGRA bytes for direct row/whole-buffer copies (e.g. into a WriteableBitmap).</summary>
    public ReadOnlySpan<byte> Bgra => _bgra;

    /// <summary>The raw backing array (not a copy) - for callers that need to write it out
    /// wholesale (<see cref="FacetColorMapDiskCache"/>) without an extra allocation/copy.</summary>
    internal byte[] RawBgraArray => _bgra;

    public void Sample(int x, int y, out byte r, out byte g, out byte b)
    {
        if ((uint)x >= (uint)WidthTiles || (uint)y >= (uint)HeightTiles)
        {
            r = g = b = 0;
            return;
        }

        long i = ((long)y * WidthTiles + x) * 4;
        b = _bgra[i + 0];
        g = _bgra[i + 1];
        r = _bgra[i + 2];
    }
}
