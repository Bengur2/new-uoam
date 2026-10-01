namespace NewUOAM.MapData.Maps;

/// <summary>
/// Which statics a tile shows over the land - old UOAM's Normal view / X-ray view / Hide statics.
/// The rules were read off old UOAM's own saved renders (MAP0-1/-X1/-NS1.BMP) by comparing them
/// tile by tile with the client files they were made from, not taken from its docs:
/// <list type="bullet">
/// <item><see cref="Normal"/>: the topmost static (highest Z, the later one in statics.mul on a
/// tie) if its Z &gt;= the land's Z, else the land (99.997% match).</item>
/// <item><see cref="XRay"/>: the topmost static among those with Z &lt;= the land's Z (buried
/// caves, cellars), else the land; anything above ground is hidden (99.64% match).</item>
/// <item><see cref="Hidden"/>: land only (100% match).</item>
/// </list>
/// The item height from tiledata.mul plays no part (every Z+height variant matched worse).
/// </summary>
public enum StaticsView
{
    Normal,
    XRay,
    Hidden,
}
