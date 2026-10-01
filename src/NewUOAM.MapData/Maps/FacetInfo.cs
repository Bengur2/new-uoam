namespace NewUOAM.MapData.Maps;

public sealed record FacetInfo(int Index, string Name, int WidthBlocks, int HeightBlocks)
{
    public int WidthTiles => WidthBlocks * 8;
    public int HeightTiles => HeightBlocks * 8;
    public long TotalBlocks => (long)WidthBlocks * HeightBlocks;

    /// <summary>
    /// Standard facet dimensions as shipped by the original client. Used as a fallback / for
    /// deriving height from a measured total block count when a facet has no raw .mul file to
    /// measure directly (UOP-only installs). Shards that resize a facet (e.g. extend Felucca)
    /// are handled by trusting the measured block count over these width/height pairs where
    /// they disagree - see <see cref="MapFacet"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, FacetInfo> Known = new Dictionary<int, FacetInfo>
    {
        [0] = new(0, "Felucca", 768, 512),
        [1] = new(1, "Trammel", 896, 512),
        [2] = new(2, "Ilshenar", 288, 200),
        [3] = new(3, "Malas", 320, 256),
        [4] = new(4, "Tokuno", 181, 181),
        [5] = new(5, "TerMur", 160, 512),
    };
}
