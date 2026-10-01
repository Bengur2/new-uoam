namespace NewUOAM.MapData.Maps;

/// <summary>
/// Raw access to 196-byte legacy map blocks (4-byte block header + 64 x 3-byte land cells),
/// regardless of whether they physically live in a flat .mul file or inside a .uop package.
/// </summary>
public interface IMapBlockSource : IDisposable
{
    /// <summary>Total number of 8x8-tile blocks actually available from this source.</summary>
    long BlockCount { get; }

    /// <summary>Reads the raw 196-byte record for the given block index into <paramref name="destination"/>.</summary>
    void ReadBlockRaw(long blockIndex, Span<byte> destination);
}
